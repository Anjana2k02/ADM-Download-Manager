using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ADM.Core;

public sealed class DownloadManager : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private readonly HttpClient http;
    private readonly string statePath;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, (CancellationTokenSource Cancel, Task Task)> active = [];
    private readonly Dictionary<Guid, (long Bytes, long Timestamp)> samples = [];
    private readonly BandwidthLimiter globalLimiter = new();
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private SavedState state = new();
    private Task? scheduler;
    private bool stopping;
    public string? RecoveryNotice { get; private set; }
    public event Action<Guid>? DownloadCompleted;

    public DownloadManager(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        statePath = Path.Combine(dataDirectory, "downloads.json");
        http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ADM/1.0");
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        Load();
    }

    public AppSettings Settings { get { lock (gate) return Clone(state.Settings); } }
    public IReadOnlyList<DownloadItem> Snapshot() { lock (gate) return state.Downloads.Select(CloneItem).ToList(); }
    private T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, json), json)!;
    private DownloadItem CloneItem(DownloadItem value) { var copy = Clone(value); copy.BytesPerSecond = value.BytesPerSecond; return copy; }

    private void Load()
    {
        if (!File.Exists(statePath)) return;
        try { state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(statePath), json) ?? throw new JsonException(); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            File.Copy(statePath, statePath + ".corrupt-" + DateTime.UtcNow.Ticks, false);
            try { state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(statePath + ".bak"), json) ?? new(); }
            catch (Exception backupEx) when (backupEx is JsonException or IOException) { state = new(); }
            RecoveryNotice = "ADM recovered from an unreadable queue file. The original file was preserved in the app data folder.";
        }
        foreach (var item in state.Downloads)
        {
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Pausing or DownloadStatus.Queued or DownloadStatus.Verifying)
            { item.Status = DownloadStatus.Paused; item.Error = "Session restored. Resume when ready."; }
        }
        state.Settings.MaxConcurrentDownloads = Math.Clamp(state.Settings.MaxConcurrentDownloads, 1, 8);
        state.Settings.ConnectionsPerDownload = Math.Clamp(state.Settings.ConnectionsPerDownload, 1, 16);
        state.Settings.RetryCount = Math.Clamp(state.Settings.RetryCount, 0, 8);
    }

    public void StartScheduler() { lock (gate) scheduler ??= Task.Run(SchedulerLoop); }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DownloadFolder) || !Path.IsPathFullyQualified(settings.DownloadFolder)) throw new ArgumentException("Choose an absolute download folder.");
        Directory.CreateDirectory(settings.DownloadFolder);
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 8);
        settings.ConnectionsPerDownload = Math.Clamp(settings.ConnectionsPerDownload, 1, 16);
        settings.GlobalSpeedLimitKbps = Math.Clamp(settings.GlobalSpeedLimitKbps, 0, 1_000_000);
        settings.RetryCount = Math.Clamp(settings.RetryCount, 0, 8);
        lock (gate) state.Settings = Clone(settings);
        await SaveAsync();
    }

    public async Task<Guid> AddAsync(AddDownloadRequest request)
    {
        if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) throw new ArgumentException("Enter a direct HTTP or HTTPS download URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("URLs with embedded passwords are not supported.");
        var hash = request.ExpectedSha256?.Trim();
        if (!string.IsNullOrEmpty(hash) && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.");
        DownloadItem item;
        lock (gate)
        {
            if (state.Downloads.Any(d => d.Url == uri.AbsoluteUri && d.Status != DownloadStatus.Completed)) throw new ArgumentException("This URL is already in your queue.");
            var name = FileCategories.SafeName(string.IsNullOrWhiteSpace(request.FileName) ? Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()) : request.FileName);
            var category = FileCategories.Get(name);
            var folder = string.IsNullOrWhiteSpace(request.Folder) ? state.Settings.DownloadFolder : request.Folder.Trim();
            if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Choose an absolute download folder.");
            // An explicit destination is respected exactly; category folders apply to the default library.
            if (string.IsNullOrWhiteSpace(request.Folder) && state.Settings.AutoCategorize) folder = Path.Combine(folder, category);
            Directory.CreateDirectory(folder);
            name = UniqueName(folder, name);
            item = new DownloadItem
            {
                Url = uri.AbsoluteUri, FileName = name, Category = category, Destination = Path.GetFullPath(folder),
                UseServerFileName = string.IsNullOrWhiteSpace(request.FileName),
                AutoCategoryRoot = string.IsNullOrWhiteSpace(request.Folder) && state.Settings.AutoCategorize ? state.Settings.DownloadFolder : null,
                Connections = state.Settings.ConnectionsPerDownload, ScheduledAt = request.ScheduledAt,
                ExpectedSha256 = string.IsNullOrEmpty(hash) ? null : hash.ToLowerInvariant(),
                Status = request.ScheduledAt > DateTimeOffset.Now ? DownloadStatus.Scheduled : request.StartNow ? DownloadStatus.Queued : DownloadStatus.Paused
            };
            state.Downloads.Add(item);
        }
        await SaveAsync();
        return item.Id;
    }

    private string UniqueName(string folder, string name, Guid? excludeId = null)
    {
        var result = name;
        for (var n = 1; File.Exists(Path.Combine(folder, result)) || state.Downloads.Any(d => d.Id != excludeId && string.Equals(d.FullPath, Path.Combine(folder, result), StringComparison.OrdinalIgnoreCase)); n++)
            result = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
        return result;
    }

    public async Task ResumeAsync(Guid id)
    {
        lock (gate)
        {
            var item = Find(id);
            if (active.ContainsKey(id) || item.Status == DownloadStatus.Completed) return;
            item.Status = DownloadStatus.Queued; item.ScheduledAt = null; item.Error = null;
        }
        await SaveAsync();
    }

    public async Task PauseAsync(Guid id)
    {
        Task? task = null;
        lock (gate)
        {
            var item = Find(id);
            if (item.Status == DownloadStatus.Completed) return;
            if (active.TryGetValue(id, out var running))
            { item.Status = DownloadStatus.Pausing; running.Cancel.Cancel(); task = running.Task; }
            else { item.Status = DownloadStatus.Paused; item.BytesPerSecond = 0; }
        }
        if (task != null) await task;
        await SaveAsync();
    }

    public async Task PauseAllAsync()
    {
        // Mark queued jobs first, so scheduler cannot fill a slot while another job pauses.
        List<Task> tasks = [];
        lock (gate)
        {
            foreach (var item in state.Downloads.Where(d => d.Status is DownloadStatus.Queued or DownloadStatus.Scheduled)) item.Status = DownloadStatus.Paused;
            foreach (var (id, running) in active) { Find(id).Status = DownloadStatus.Pausing; running.Cancel.Cancel(); tasks.Add(running.Task); }
        }
        await Task.WhenAll(tasks);
        await SaveAsync();
    }

    public async Task RemoveAsync(Guid id)
    {
        await PauseAsync(id);
        lock (gate)
        {
            var item = Find(id);
            DeleteParts(item);
            state.Downloads.Remove(item); samples.Remove(id);
        }
        await SaveAsync();
    }

    public async Task RestartAsync(Guid id)
    {
        await PauseAsync(id);
        lock (gate)
        {
            var item = Find(id);
            if (item.Status == DownloadStatus.Completed) throw new InvalidOperationException("Add the URL again to download another copy.");
            DeleteParts(item); item.Parts.Clear(); item.DownloadedBytes = 0; item.TotalBytes = null;
            item.Error = null; item.ETag = null; item.LastModified = null; item.Status = DownloadStatus.Queued;
        }
        await SaveAsync();
    }

    public async Task SetSpeedLimitAsync(Guid id, int kibPerSecond)
    { lock (gate) Find(id).SpeedLimitKbps = Math.Clamp(kibPerSecond, 0, 1_000_000); await SaveAsync(); }

    private DownloadItem Find(Guid id) => state.Downloads.FirstOrDefault(d => d.Id == id) ?? throw new ArgumentException("Download no longer exists.");
    private static string PartsFolder(DownloadItem item) => Path.Combine(item.Destination, ".adm-parts", item.Id.ToString("N"));
    private static string PartPath(DownloadItem item, DownloadPart part) => Path.Combine(PartsFolder(item), part.Index + ".part");
    private static void DeleteParts(DownloadItem item) { var folder = PartsFolder(item); if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    public async Task SaveAsync()
    {
        await saveGate.WaitAsync();
        try
        {
            string content;
            lock (gate) content = JsonSerializer.Serialize(state, json);
            await File.WriteAllTextAsync(statePath + ".tmp", content);
            if (File.Exists(statePath)) File.Replace(statePath + ".tmp", statePath, statePath + ".bak");
            else File.Move(statePath + ".tmp", statePath);
        }
        finally { saveGate.Release(); }
    }

    private async Task SchedulerLoop()
    {
        var lastSave = Stopwatch.GetTimestamp();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                lock (gate)
                {
                    if (stopping) break;
                    foreach (var item in state.Downloads)
                    {
                        if (item.Status == DownloadStatus.Scheduled && item.ScheduledAt <= DateTimeOffset.Now) item.Status = DownloadStatus.Queued;
                        if (item.Status == DownloadStatus.Downloading)
                        {
                            var now = Stopwatch.GetTimestamp();
                            if (samples.TryGetValue(item.Id, out var prev)) item.BytesPerSecond = Math.Max(0, item.DownloadedBytes - prev.Bytes) / Stopwatch.GetElapsedTime(prev.Timestamp, now).TotalSeconds;
                            samples[item.Id] = (item.DownloadedBytes, now);
                        }
                        else { item.BytesPerSecond = 0; samples.Remove(item.Id); }
                    }
                    foreach (var item in state.Downloads.Where(d => d.Status == DownloadStatus.Queued && !active.ContainsKey(d.Id)).Take(Math.Max(0, state.Settings.MaxConcurrentDownloads - active.Count)).ToList())
                    {
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        item.Status = DownloadStatus.Connecting;
                        active[item.Id] = (cts, Task.Run(() => RunAsync(item, cts.Token)));
                    }
                }
                if (Stopwatch.GetElapsedTime(lastSave).TotalSeconds >= 2)
                {
                    try { await SaveAsync(); } catch (IOException) { /* Transfers remain usable when a transient state save fails. */ }
                    lastSave = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("The server did not respond within 30 seconds."); }
    }

    private async Task RunAsync(DownloadItem item, CancellationToken token)
    {
        var completed = false;
        try
        {
            int retries; lock (gate) retries = state.Settings.RetryCount;
            for (var attempt = 0; ; attempt++)
            {
                try { await TransferAsync(item, token); break; }
                catch (Exception ex) when ((ex is HttpRequestException or IOException) && ex is not InvalidDataException && attempt < retries && !token.IsCancellationRequested)
                {
                    lock (gate) { item.Error = $"Retry {attempt + 1}/{retries}: {ex.Message}"; item.Status = DownloadStatus.Connecting; }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1))), token);
                }
            }
            token.ThrowIfCancellationRequested();
            lock (gate) { item.Status = DownloadStatus.Verifying; item.BytesPerSecond = 0; }
            await AssembleAsync(item, token);
            lock (gate) { item.Status = DownloadStatus.Completed; item.CompletedAt = DateTimeOffset.Now; item.Error = null; completed = true; }
            try { DeleteParts(item); } catch (IOException) { /* Final file is already committed. */ } catch (UnauthorizedAccessException) { }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { lock (gate) { item.Status = DownloadStatus.Paused; item.Error = null; } }
        catch (Exception ex)
        { lock (gate) { item.Status = DownloadStatus.Failed; item.Error = ex.Message; } }
        finally
        {
            try { await SaveAsync(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RecoveryNotice = "Could not save the queue: " + ex.Message; }
            lock (gate)
            {
                item.BytesPerSecond = 0;
                if (active.Remove(item.Id, out var running)) running.Cancel.Dispose();
            }
        }
        if (completed) DownloadCompleted?.Invoke(item.Id);
    }

    private async Task TransferAsync(DownloadItem item, CancellationToken token)
    {
        using var probeRequest = new HttpRequestMessage(HttpMethod.Get, item.Url);
        probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
        using var probe = await SendAsync(probeRequest, token);
        // A zero-byte resource may answer a range probe with 416.
        var empty = probe.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && probe.Content.Headers.ContentRange?.Length == 0;
        if (!empty) probe.EnsureSuccessStatusCode();
        var range = probe.Content.Headers.ContentRange;
        var supportsRange = probe.StatusCode == HttpStatusCode.PartialContent && range?.From == 0 && range.To == 0 && range.Length > 0;
        if (probe.StatusCode == HttpStatusCode.PartialContent && !supportsRange) throw new InvalidDataException("The server returned an invalid byte range.");
        if (probe.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The server sent compressed content despite a byte-exact request.");
        var total = empty ? 0 : supportsRange ? range!.Length : probe.Content.Headers.ContentLength;
        var etag = probe.Headers.ETag is { IsWeak: false } strongTag ? strongTag.ToString() : null;
        var modified = probe.Content.Headers.LastModified;
        lock (gate)
        {
            if (item.UseServerFileName && item.Parts.Count == 0)
            {
                var disposition = probe.Content.Headers.ContentDisposition;
                var serverName = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
                if (string.IsNullOrWhiteSpace(serverName) && Path.GetExtension(item.FileName).Length == 0)
                    serverName = Uri.UnescapeDataString(probe.RequestMessage?.RequestUri?.AbsolutePath.Split('/').Last() ?? "");
                if (!string.IsNullOrWhiteSpace(serverName))
                {
                    var name = FileCategories.SafeName(serverName);
                    item.Category = FileCategories.Get(name);
                    if (item.AutoCategoryRoot != null) item.Destination = Path.Combine(item.AutoCategoryRoot, item.Category);
                    item.FileName = UniqueName(item.Destination, name, item.Id);
                }
            }
        }
        Directory.CreateDirectory(PartsFolder(item));
        lock (gate)
        {
            var sameVersion = etag != null ? etag == item.ETag : modified != null && item.ETag == null && modified == item.LastModified;
            var reusable = supportsRange && sameVersion && item.TotalBytes == total && item.Parts.Count > 0;
            if (!reusable)
            {
                DeleteParts(item); Directory.CreateDirectory(PartsFolder(item));
                item.Parts.Clear(); item.DownloadedBytes = 0;
                int count = supportsRange && total >= 2 * 1024 * 1024 ? Math.Clamp(item.Connections, 1, 16) : 1;
                long chunk = total.GetValueOrDefault() / count;
                for (var i = 0; i < count; i++) item.Parts.Add(new DownloadPart { Index = i, Start = i * chunk, End = total.HasValue ? (i == count - 1 ? total - 1 : (i + 1) * chunk - 1) : null });
            }
            item.ETag = etag; item.LastModified = modified; item.TotalBytes = total; item.SupportsResume = supportsRange && (etag != null || modified != null);
            item.DownloadedBytes = item.Parts.Sum(p => File.Exists(PartPath(item, p)) ? new FileInfo(PartPath(item, p)).Length : 0);
            item.Status = DownloadStatus.Downloading; item.Error = null;
        }
        await SaveAsync(); // Persist the part map before writing bytes, enabling crash recovery.
        if (total == 0) { await File.WriteAllBytesAsync(PartPath(item, item.Parts[0]), [], token); return; }
        using var group = CancellationTokenSource.CreateLinkedTokenSource(token);
        var limiter = new BandwidthLimiter();
        var tasks = item.Parts.Select(async part =>
        {
            try { await DownloadPartAsync(item, part, supportsRange, limiter, group.Token); }
            catch { await group.CancelAsync(); throw; }
        }).ToArray();
        try { await Task.WhenAll(tasks); }
        catch
        {
            // Preserve the actual range/network failure rather than a sibling cancellation.
            var failure = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).FirstOrDefault(e => e is not OperationCanceledException);
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private async Task DownloadPartAsync(DownloadItem item, DownloadPart part, bool rangeSupported, BandwidthLimiter limiter, CancellationToken token)
    {
        var path = PartPath(item, part);
        long existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        var expected = part.End - part.Start + 1;
        if (expected.HasValue && existing > expected.Value) throw new InvalidDataException("A temporary part is larger than expected. Use Restart to download a clean copy.");
        if (expected.HasValue && existing == expected.Value) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        if (rangeSupported)
        {
            request.Headers.Range = new RangeHeaderValue(part.Start + existing, part.End);
            if (item.ETag != null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(item.ETag));
            else if (item.LastModified.HasValue) request.Headers.IfRange = new RangeConditionHeaderValue(item.LastModified.Value);
        }
        using var response = await SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        if (rangeSupported)
        {
            var received = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || received?.From != part.Start + existing || received.To != part.End || received.Length != item.TotalBytes)
                throw new InvalidDataException("The server changed the file or ignored its byte range. Use Restart to download a clean copy.");
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent) throw new InvalidDataException("The server returned an unexpected partial response.");
        if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Unexpected compressed response; byte-exact download cannot continue.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[32 * 1024];
        long receivedBytes = existing;
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            int read;
            try { read = await source.ReadAsync(buffer, readTimeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("The connection stopped sending data for 30 seconds."); }
            if (read == 0) break;
            if (expected.HasValue && receivedBytes + read > expected.Value) throw new InvalidDataException("The server sent more data than its declared size.");
            await globalLimiter.WaitAsync(read, () => { lock (gate) return state.Settings.GlobalSpeedLimitKbps; }, token);
            await limiter.WaitAsync(read, () => { lock (gate) return item.SpeedLimitKbps; }, token);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            receivedBytes += read;
            lock (gate) item.DownloadedBytes += read;
        }
        await output.FlushAsync(token);
        if (expected.HasValue && receivedBytes != expected.Value) throw new IOException("The connection ended before all bytes were received.");
        if (!item.TotalBytes.HasValue) lock (gate) item.TotalBytes = item.DownloadedBytes;
    }

    private async Task AssembleAsync(DownloadItem item, CancellationToken token)
    {
        var assembled = Path.Combine(PartsFolder(item), "assembled.tmp");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = new FileStream(assembled, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[131072];
            foreach (var part in item.Parts.OrderBy(p => p.Index))
            {
                await using var input = new FileStream(PartPath(item, part), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                { await output.WriteAsync(buffer.AsMemory(0, read), token); hash.AppendData(buffer, 0, read); }
            }
            await output.FlushAsync(token);
            if (item.TotalBytes.HasValue && output.Length != item.TotalBytes.Value) throw new InvalidDataException("Final size verification failed. Use Restart for a clean download.");
        }
        var checksum = Convert.ToHexStringLower(hash.GetHashAndReset());
        lock (gate) item.ActualSha256 = checksum;
        if (item.ExpectedSha256 != null && !checksum.Equals(item.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 mismatch. The download was not published. Check the expected hash or restart the download.");
        token.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            // Preserve Windows' normal downloaded-file security checks when the volume supports ADS.
            try { await File.WriteAllTextAsync(assembled + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", token); }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { }
        }
        lock (gate)
        {
            item.FileName = UniqueName(item.Destination, item.FileName, item.Id);
            // Atomic rename on the same volume; never overwrite an existing user file.
            File.Move(assembled, item.FullPath, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Task> tasks;
        lock (gate)
        {
            stopping = true;
            lifetime.Cancel();
            tasks = active.Values.Select(v => v.Task).ToList();
        }
        if (scheduler != null) await scheduler;
        await Task.WhenAll(tasks);
        await SaveAsync();
        http.Dispose(); lifetime.Dispose(); saveGate.Dispose();
    }
}

internal sealed class BandwidthLimiter
{
    private readonly object gate = new();
    private double last;
    private double tokens;
    private int previousRate;
    public async Task WaitAsync(int bytes, Func<int> getRate, CancellationToken token)
    {
        var remaining = bytes;
        // Consume small quanta without reserving future slots. Canceled transfers cannot
        // leave a long reservation that blocks every other download at low rate limits.
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var kibPerSecond = getRate();
            if (kibPerSecond <= 0) { lock (gate) { tokens = 0; previousRate = 0; } return; }
            double delay;
            lock (gate)
            {
                var now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                var rate = kibPerSecond * 1024.0;
                if (previousRate != kibPerSecond) { last = now; tokens = 0; previousRate = kibPerSecond; }
                var quantum = Math.Min(remaining, Math.Max(1024, rate / 10));
                tokens = Math.Min(Math.Max(32768, rate / 4), tokens + Math.Max(0, now - last) * rate);
                last = now;
                var take = (int)Math.Min(quantum, tokens);
                remaining -= take; tokens -= take;
                delay = remaining > 0 ? Math.Min(0.1, Math.Max(0.01, (quantum - take) / rate)) : 0;
            }
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token);
        }
    }
}
