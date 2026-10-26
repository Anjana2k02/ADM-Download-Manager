using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace ADM.App;

internal sealed class LinkReceiver : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task loop;
    private static string Name => "ADM.Links." + WindowsIdentity.GetCurrent().User!.Value.Replace('-', '_');
    public LinkReceiver(Action<string> onLink) => loop = Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var buffer = new byte[8193]; var total = 0;
                while (total < buffer.Length)
                {
                    var read = await server.ReadAsync(buffer.AsMemory(total), timeout.Token); if (read == 0) break; total += read;
                }
                if (total <= 8192)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, total);
                    if (text == "activate" || (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))) onLink(text);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    });
    public static async Task SendAsync(string text)
    {
        await using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await client.WriteAsync(Encoding.UTF8.GetBytes(text)); await client.FlushAsync();
    }
    public void Dispose() { cancellation.Cancel(); _ = loop.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default); }
}
