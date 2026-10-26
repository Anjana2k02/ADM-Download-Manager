using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

// Chromium native messaging: exactly one length-prefixed JSON message in and out.
// Never write diagnostics to stdout; it is the browser's framing channel.
try
{
    using var input = Console.OpenStandardInput();
    var header = new byte[4]; await input.ReadExactlyAsync(header);
    var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (length is 0 or > 65536) throw new ArgumentException("The browser request is too large.");
    var bytes = new byte[(int)length]; await input.ReadExactlyAsync(bytes);
    using var json = JsonDocument.Parse(bytes);
    var text = json.RootElement.GetProperty("url").GetString();
    if (text == null || text.Length > 8192 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length > 0)
        throw new ArgumentException("Choose a direct HTTP or HTTPS file link without embedded credentials.");
    var app = Path.Combine(AppContext.BaseDirectory, "ADM.exe");
    if (!File.Exists(app)) throw new FileNotFoundException("ADM.exe must be beside ADM.Bridge.exe.");
    Process.Start(new ProcessStartInfo(app) { UseShellExecute = false, ArgumentList = { "--add", uri.AbsoluteUri } });
    await Respond(new { ok = true });
}
catch (Exception ex) { await Respond(new { ok = false, error = ex.Message }); Environment.ExitCode = 1; }

static async Task Respond(object message)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
    var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
    using var output = Console.OpenStandardOutput();
    await output.WriteAsync(header); await output.WriteAsync(bytes); await output.FlushAsync();
}
