using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ADM.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
await using var server = builder.Build();
var payload = new byte[4 * 1024 * 1024 + 137];
new Random(2026).NextBytes(payload);
var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
var logs = new ConcurrentQueue<(string Path, long Start, long End)>();
var version = 1;
var flaky = 0;
server.Run(async context =>
{
    try
    {
        var path = context.Request.Path.Value!;
        if (path == "/redirect") { context.Response.Redirect("/named"); return; }
        if (path == "/missing") { context.Response.StatusCode = 404; return; }
        var length = path.StartsWith("/zero") ? 0 : path == "/small" ? 128 * 1024 : payload.Length;
        bool canRange = path != "/no-range" && path != "/unknown";
        if (path != "/no-validator") context.Response.Headers.ETag = $"\"test-v{version}\"";
        context.Response.ContentType = "application/octet-stream";
        if (path == "/named") context.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''Research%20notes.pdf";
        long from = 0, to = length - 1;
        var rangeHeader = context.Request.Headers.Range.ToString();
        if (canRange && rangeHeader.StartsWith("bytes="))
        {
            if (length == 0)
            { context.Response.StatusCode = 416; context.Response.Headers.ContentRange = "bytes */0"; return; }
            var range = rangeHeader[6..].Split('-'); from = long.Parse(range[0]); to = range[1].Length == 0 ? length - 1 : long.Parse(range[1]);
            if (from >= length) { context.Response.StatusCode = 416; return; }
            context.Response.StatusCode = 206;
            context.Response.Headers.ContentRange = $"bytes {((path == "/bad-range" && to != 0) ? from + 1 : from)}-{to}/{length}";
        }
        logs.Enqueue((path, from, to));
        if (path != "/unknown") context.Response.ContentLength = to - from + 1;
        bool abort = path == "/flaky" && to > 0 && Interlocked.Increment(ref flaky) == 1;
        for (long offset = from; offset <= to;)
        {
            var count = (int)Math.Min(16 * 1024, to - offset + 1);
            await context.Response.Body.WriteAsync(payload.AsMemory((int)offset, count), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            offset += count;
            if (abort && offset - from >= 128 * 1024) { context.Abort(); return; }
            if (to > 0 && path != "/small") await Task.Delay(path.StartsWith("/slow") || path == "/mutable" ? 14 : 2, context.RequestAborted);
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
});
await server.StartAsync();
var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var root = Path.Combine(Path.GetTempPath(), "ADM-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0;

async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS  " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL  " + name + "\n      " + ex); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task<DownloadManager> Manager(string name, int connections = 4)
{
    var manager = new DownloadManager(Path.Combine(root, name, "state"));
    await manager.UpdateSettingsAsync(new AppSettings { DownloadFolder = Path.Combine(root, name, "files"), ConnectionsPerDownload = connections, RetryCount = 1, MaxConcurrentDownloads = 2 });
    manager.StartScheduler(); return manager;
}
async Task<DownloadItem> Wait(DownloadManager manager, Guid id, Func<DownloadItem, bool> predicate, int seconds = 20)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        var item = manager.Snapshot().Single(d => d.Id == id);
        if (predicate(item)) return item;
        if (item.Status == DownloadStatus.Failed) throw new Exception(item.Error);
        await Task.Delay(25);
    }
    throw new TimeoutException("Download did not reach the expected state.");
}
async Task Verify(DownloadItem item)
{
    Check(item.Status == DownloadStatus.Completed, "Not completed");
    Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(item.FullPath))) == hash, "Final content differs");
    Check(item.ActualSha256 == hash, "Stored hash differs");
}

await Test("Segmented transfer is byte-exact, including uneven final segment", async () =>
{
    await using var m = await Manager("segmented");
    var id = await m.AddAsync(new(address + "/range", "data.zip", ExpectedSha256: hash));
    var item = await Wait(m, id, d => d.Status == DownloadStatus.Completed);
    Check(item.Parts.Count == 4, "Expected four segments"); Check(item.Category == "Archives", "Wrong category");
    Check(item.Destination.EndsWith("Archives"), "Category folder missing"); await Verify(item);
    if (OperatingSystem.IsWindows()) Check(File.ReadAllText(item.FullPath + ":Zone.Identifier").Contains("ZoneId=3"), "Internet zone missing");
});
await Test("Pause/resume preserves parts and requests remaining byte ranges", async () =>
{
    await using var m = await Manager("pause");
    var id = await m.AddAsync(new(address + "/slow-pause", "video.mp4"));
    await Wait(m, id, d => d.DownloadedBytes > 256 * 1024); await m.PauseAsync(id);
    var paused = m.Snapshot().Single(); Check(paused.Status == DownloadStatus.Paused, "Pause failed");
    var before = logs.Count; await m.ResumeAsync(id);
    await Verify(await Wait(m, id, d => d.Status == DownloadStatus.Completed));
    Check(logs.Skip(before).Any(r => r.Path == "/slow-pause" && r.Start > 0 && r.Start < payload.Length / 4), "No resumed first-segment request");
});
await Test("Queue and resumable parts survive application restart", async () =>
{
    var m = await Manager("persist");
    var id = await m.AddAsync(new(address + "/slow-persist", "saved.zip"));
    await Wait(m, id, d => d.DownloadedBytes > 128 * 1024); await m.DisposeAsync();
    await using var restored = new DownloadManager(Path.Combine(root, "persist", "state"));
    Check(restored.Snapshot().Single().Status == DownloadStatus.Paused, "Restored transfer should be paused");
    restored.StartScheduler(); await restored.ResumeAsync(id);
    await Verify(await Wait(restored, id, d => d.Status == DownloadStatus.Completed));
});
await Test("Servers without byte ranges restart safely", async () =>
{
    await using var m = await Manager("no-range");
    var id = await m.AddAsync(new(address + "/no-range", "plain.bin"));
    await Wait(m, id, d => d.DownloadedBytes > 128 * 1024); await m.PauseAsync(id); await m.ResumeAsync(id);
    var done = await Wait(m, id, d => d.Status == DownloadStatus.Completed); Check(!done.SupportsResume, "Incorrect resume claim"); await Verify(done);
});
await Test("Unknown-length responses complete correctly", async () =>
{
    await using var m = await Manager("unknown"); var id = await m.AddAsync(new(address + "/unknown", "stream.bin"));
    await Verify(await Wait(m, id, d => d.Status == DownloadStatus.Completed));
});
await Test("Empty resources complete with a verified empty file", async () =>
{
    await using var m = await Manager("zero"); var id = await m.AddAsync(new(address + "/zero", "empty.txt"));
    var item = await Wait(m, id, d => d.Status == DownloadStatus.Completed); Check(new FileInfo(item.FullPath).Length == 0, "Not empty");
});
await Test("Invalid server ranges never publish a corrupted file", async () =>
{
    await using var m = await Manager("bad"); var id = await m.AddAsync(new(address + "/bad-range", "bad.bin"));
    var item = await Wait(m, id, d => d.Status == DownloadStatus.Failed); Check(!File.Exists(item.FullPath), "Corrupted file was published");
});
await Test("Expected SHA-256 mismatch blocks publication", async () =>
{
    await using var m = await Manager("checksum"); var id = await m.AddAsync(new(address + "/range", "bad.bin", ExpectedSha256: new string('0', 64)));
    var item = await Wait(m, id, d => d.Status == DownloadStatus.Failed); Check(item.Error!.Contains("SHA-256"), "Wrong failure"); Check(!File.Exists(item.FullPath), "Bad checksum file published");
});
await Test("Changed ETag discards stale partial bytes", async () =>
{
    await using var m = await Manager("changed"); var id = await m.AddAsync(new(address + "/mutable", "changed.zip"));
    await Wait(m, id, d => d.DownloadedBytes > 128 * 1024); await m.PauseAsync(id); Interlocked.Increment(ref version);
    var before = logs.Count; await m.ResumeAsync(id); await Verify(await Wait(m, id, d => d.Status == DownloadStatus.Completed));
    Check(logs.Skip(before).Any(r => r.Path == "/mutable" && r.Start == 0 && r.End > 0), "Stale first segment was reused");
});
await Test("Content-Disposition and redirects determine automatic category", async () =>
{
    await using var m = await Manager("named"); var id = await m.AddAsync(new(address + "/redirect"));
    var item = await Wait(m, id, d => d.Status == DownloadStatus.Completed);
    Check(item.FileName == "Research notes.pdf" && item.Category == "Documents" && item.Destination.EndsWith("Documents"), "Server filename or category ignored"); await Verify(item);
});
await Test("Explicit destinations and filenames override automatic organization", async () =>
{
    await using var m = await Manager("custom"); var folder = Path.Combine(root, "custom-exact");
    var id = await m.AddAsync(new(address + "/named", "chosen.mp4", folder));
    var item = await Wait(m, id, d => d.Status == DownloadStatus.Completed);
    Check(item.FileName == "chosen.mp4" && item.Destination == folder, "Explicit path/name changed"); await Verify(item);
});
await Test("Existing files are never overwritten", async () =>
{
    await using var m = await Manager("collision"); var folder = m.Settings.DownloadFolder;
    await File.WriteAllTextAsync(Path.Combine(folder, "same.bin"), "keep me");
    var id = await m.AddAsync(new(address + "/range", "same.bin", folder)); var item = await Wait(m, id, d => d.Status == DownloadStatus.Completed);
    Check(item.FileName == "same (1).bin", "Collision naming failed"); Check(await File.ReadAllTextAsync(Path.Combine(folder, "same.bin")) == "keep me", "Existing file overwritten");
});
await Test("Interrupted transfers retry automatically and remain byte-exact", async () =>
{
    await using var m = await Manager("retry", 1); var id = await m.AddAsync(new(address + "/flaky", "retried.bin"));
    await Verify(await Wait(m, id, d => d.Status == DownloadStatus.Completed)); Check(flaky >= 2, "No retry occurred");
});
await Test("Scheduling waits until the requested time", async () =>
{
    await using var m = await Manager("schedule"); var id = await m.AddAsync(new(address + "/small", "later.bin", ScheduledAt: DateTimeOffset.Now.AddSeconds(1.5)));
    await Task.Delay(500); Check(m.Snapshot().Single().Status == DownloadStatus.Scheduled, "Started too early");
    await Wait(m, id, d => d.Status == DownloadStatus.Completed);
});
await Test("Global speed cap applies to actual transfer throughput", async () =>
{
    await using var m = await Manager("throttle"); var settings = m.Settings; settings.GlobalSpeedLimitKbps = 64; await m.UpdateSettingsAsync(settings);
    var watch = Stopwatch.StartNew(); var id = await m.AddAsync(new(address + "/small", "limited.bin"));
    await Wait(m, id, d => d.Status == DownloadStatus.Completed);
    Check(watch.Elapsed.TotalSeconds >= 1.8, "Transfer exceeded the configured speed cap");
});
await Test("Pause all also stops queued and scheduled work", async () =>
{
    await using var m = await Manager("pause-all"); var settings = m.Settings; settings.MaxConcurrentDownloads = 1; await m.UpdateSettingsAsync(settings);
    var first = await m.AddAsync(new(address + "/slow-all", "first.zip"));
    await m.AddAsync(new(address + "/slow-queued", "second.zip"));
    await m.AddAsync(new(address + "/small", "scheduled.zip", ScheduledAt: DateTimeOffset.Now.AddMinutes(1)));
    await Wait(m, first, d => d.DownloadedBytes > 0); await m.PauseAllAsync(); await Task.Delay(400);
    Check(m.Snapshot().All(d => d.Status == DownloadStatus.Paused), "Work restarted after pause all");
});
await Test("Removing history preserves completed files", async () =>
{
    await using var m = await Manager("remove"); var id = await m.AddAsync(new(address + "/small", "keep.bin"));
    var item = await Wait(m, id, d => d.Status == DownloadStatus.Completed); await m.RemoveAsync(id);
    Check(m.Snapshot().Count == 0 && File.Exists(item.FullPath), "History removal deleted the file");
});
await Test("Missing files and duplicate URLs produce actionable failures", async () =>
{
    await using var m = await Manager("validation"); var id = await m.AddAsync(new(address + "/missing", "missing.bin"));
    var item = await Wait(m, id, d => d.Status == DownloadStatus.Failed); Check(item.Error!.Contains("404"), "HTTP status missing");
    try { await m.AddAsync(new(address + "/missing")); throw new Exception("Duplicate accepted"); } catch (ArgumentException) { }
    try { await m.AddAsync(new("file:///C:/Windows/win.ini")); throw new Exception("Invalid protocol accepted"); } catch (ArgumentException) { }
});
await Test("Unsafe filenames are sanitized and categories cover common formats", () =>
{
    Check(FileCategories.SafeName("../../CON.exe") == "_CON.exe", "Reserved filename not sanitized");
    Check(FileCategories.SafeName("report:final?.pdf") == "report_final_.pdf", "Invalid characters retained");
    Check(FileCategories.Get("MOVIE.MKV") == "Video" && FileCategories.Get("archive.tar.gz") == "Archives", "Category mapping failed");
    return Task.CompletedTask;
});
await Test("A speed limit can be disabled while a slow read is throttled", async () =>
{
    await using var m = await Manager("live-limit");
    var settings = m.Settings; settings.GlobalSpeedLimitKbps = 1; await m.UpdateSettingsAsync(settings);
    var id = await m.AddAsync(new(address + "/small", "live.bin"));
    await Wait(m, id, d => d.Status == DownloadStatus.Downloading);
    await Task.Delay(300); settings.GlobalSpeedLimitKbps = 0; await m.UpdateSettingsAsync(settings);
    await Wait(m, id, d => d.Status == DownloadStatus.Completed, 3);
});
await Test("Per-download speed limit is enforced", async () =>
{
    await using var m = await Manager("item-limit");
    var id = await m.AddAsync(new(address + "/small", "individual.bin", StartNow: false));
    await m.SetSpeedLimitAsync(id, 64); var watch = Stopwatch.StartNew(); await m.ResumeAsync(id);
    await Wait(m, id, d => d.Status == DownloadStatus.Completed);
    Check(watch.Elapsed.TotalSeconds >= 1.8, "Per-file speed cap was ignored");
});
await Test("Resume without validators restarts safely", async () =>
{
    await using var m = await Manager("no-validator"); var id = await m.AddAsync(new(address + "/no-validator", "novalidator.bin"));
    await Wait(m, id, d => d.DownloadedBytes > 128 * 1024); await m.PauseAsync(id);
    var before = logs.Count; await m.ResumeAsync(id); var done = await Wait(m, id, d => d.Status == DownloadStatus.Completed);
    Check(!done.SupportsResume, "Unsafe resume was advertised");
    Check(logs.Skip(before).Any(r => r.Path == "/no-validator" && r.Start == 0 && r.End > 0), "Unsafe partial was reused"); await Verify(done);
});
await Test("Concurrency never exceeds the configured number of downloads", async () =>
{
    await using var m = await Manager("concurrency"); var settings = m.Settings; settings.MaxConcurrentDownloads = 1; await m.UpdateSettingsAsync(settings);
    var first = await m.AddAsync(new(address + "/slow-concurrent-one", "one.zip"));
    var second = await m.AddAsync(new(address + "/slow-concurrent-two", "two.zip"));
    await Wait(m, first, d => d.DownloadedBytes > 0);
    Check(m.Snapshot().Single(d => d.Id == second).Status == DownloadStatus.Queued, "Second download started without a slot");
    await Wait(m, first, d => d.Status == DownloadStatus.Completed); await Wait(m, second, d => d.Status == DownloadStatus.Completed);
});
await Test("Corrupt state recovers from backup without deleting the original", async () =>
{
    var m = await Manager("recovery"); await m.AddAsync(new(address + "/small", "saved.bin", StartNow: false));
    await m.SaveAsync(); await m.DisposeAsync();
    var stateFolder = Path.Combine(root, "recovery", "state"); await File.WriteAllTextAsync(Path.Combine(stateFolder, "downloads.json"), "broken json");
    await using var recovered = new DownloadManager(stateFolder);
    Check(recovered.Snapshot().Count == 1 && recovered.RecoveryNotice != null, "Backup recovery failed");
    Check(Directory.GetFiles(stateFolder, "*.corrupt-*").Length == 1, "Corrupt state was not preserved");
});
if (args.Contains("--https-smoke"))
{
    await Test("Public HTTPS download completes with TLS and real response headers", async () =>
    {
        await using var m = await Manager("https-smoke");
        var id = await m.AddAsync(new("https://www.rfc-editor.org/rfc/rfc9110.txt"));
        var item = await Wait(m, id, d => d.Status == DownloadStatus.Completed, 90);
        Check(item.DownloadedBytes > 50000, "Unexpectedly small response");
        Check((await File.ReadAllTextAsync(item.FullPath)).Contains("HTTP Semantics"), "Unexpected response content");
    });
}
await server.StopAsync();
Console.WriteLine($"\n{passed} passed; {failed} failed. Test artifacts: {root}");
Environment.ExitCode = failed == 0 ? 0 : 1;
