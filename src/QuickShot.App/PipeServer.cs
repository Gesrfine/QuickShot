using System.IO.Pipes;
using System.Text.Json.Nodes;
using QuickShot.Core;

namespace QuickShot.App;

internal sealed class PipePeer(NamedPipeServerStream stream) : IDisposable
{
    internal readonly Guid Id = Guid.NewGuid();
    private readonly SemaphoreSlim _writeLock = new(1);
    internal async Task SendAsync(JsonObject message)
    {
        await _writeLock.WaitAsync();
        try { await Protocol.WriteAsync(stream, message, CancellationToken.None); }
        finally { _writeLock.Release(); }
    }
    internal Task<JsonObject?> ReadAsync(CancellationToken token) => Protocol.ReadAsync(stream, token);
    public void Dispose() => stream.Dispose();
}

internal sealed class PipeServer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<PipePeer> _peers = [];
    internal event Action<PipePeer, JsonObject>? Message;
    internal event Action<PipePeer>? Disconnected;

    internal void Start() => _ = AcceptLoop();

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = new NamedPipeServerStream(Program.PipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(_stop.Token);
                var peer = new PipePeer(stream);
                lock (_peers) _peers.Add(peer);
                _ = ReadLoop(peer);
            }
            catch (OperationCanceledException) { stream?.Dispose(); break; }
            catch (IOException e) { stream?.Dispose(); LocalLog.Write("pipe", e); await Task.Delay(1000, _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        }
    }

    private async Task ReadLoop(PipePeer peer)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await peer.ReadAsync(_stop.Token);
                if (message == null) break;
                Message?.Invoke(peer, message);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) LocalLog.Write("pipe-read", e); }
        finally
        {
            lock (_peers) _peers.Remove(peer);
            peer.Dispose();
            Disconnected?.Invoke(peer);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_peers) foreach (var peer in _peers) peer.Dispose();
    }
}
