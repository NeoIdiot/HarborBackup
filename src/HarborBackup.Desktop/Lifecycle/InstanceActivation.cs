using System.IO.Pipes;

namespace HarborBackup.Desktop.Lifecycle;

/// <summary>Accepts window activation and safe-exit requests from the current Windows user.</summary>
internal sealed class InstanceActivation : IDisposable
{
    private readonly CancellationTokenSource stopped = new();
    private readonly Task listener;
    public InstanceActivation(string name, Action show, Action? shutdown = null, Action? showIssues = null) =>
        listener = ListenAsync(name, show, shutdown, showIssues, stopped.Token);
    private static async Task ListenAsync(string name, Action show, Action? shutdown, Action? showIssues, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var command = new byte[1];
                if (await server.ReadAsync(command, deadline.Token) == 1)
                {
                    if (command[0] == 0x53) show();
                    else if (command[0] == 0x45) shutdown?.Invoke();
                    else if (command[0] == 0x49) (showIssues ?? show)();
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (OperationCanceledException) { break; }
            catch (IOException) { if (!token.IsCancellationRequested) await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }
    public static Task<bool> ShowAsync(string name) => SendAsync(name, 0x53);
    public static Task<bool> ShutdownAsync(string name) => SendAsync(name, 0x45);
    public static Task<bool> ShowIssuesAsync(string name) => SendAsync(name, 0x49);
    private static async Task<bool> SendAsync(string name, byte command)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(deadline.Token);
            await client.WriteAsync(new byte[] { command }, deadline.Token);
            await client.FlushAsync(deadline.Token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose()
    {
        stopped.Cancel();
        // No UI-thread wait: show callbacks marshal to the dispatcher.
        _ = listener.ContinueWith(t => { _ = t.Exception; stopped.Dispose(); }, TaskScheduler.Default);
    }
}
