using System.IO;

namespace RDPClipGuard.ClipBridge;

/// <summary>
/// A live, authenticated ClipBridge connection over an already-handshaked stream. Reads framed
/// messages, applies anti-replay, raises <see cref="ClipboardReceived"/> for clipboard updates,
/// and answers pings. Used by both the server and the client.
/// </summary>
internal sealed class ClipBridgeSession : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _key;
    private readonly ReplayGuard _replayGuard;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _seq;

    /// <summary>Raised with the text of an accepted (fresh, non-replayed) clipboard update.</summary>
    public event Action<string>? ClipboardReceived;

    public ClipBridgeSession(Stream stream, byte[] key, int replayToleranceSeconds)
    {
        _stream = stream;
        _key = key;
        _replayGuard = new ReplayGuard(replayToleranceSeconds);
    }

    /// <summary>
    /// Reads and dispatches messages until the peer closes, an error occurs, or cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ClipMessage msg = await ClipProtocol.ReadMessageAsync(_stream, _key, ct).ConfigureAwait(false);

            switch (msg.Type)
            {
                case MessageType.ClipboardUpdate:
                    if (msg.Text != null && _replayGuard.TryAccept(msg, ClipMessage.NowUnixMs()))
                        ClipboardReceived?.Invoke(msg.Text);
                    break;

                case MessageType.Ping:
                    await SendAsync(ClipMessage.Control(MessageType.Pong, NextSeq()), ct).ConfigureAwait(false);
                    break;

                case MessageType.Pong:
                    break; // keepalive ack — nothing to do

                default:
                    break; // ignore unexpected post-handshake message types
            }
        }
    }

    /// <summary>Sends a clipboard update to the peer.</summary>
    public Task SendClipboardAsync(string text, CancellationToken ct)
        => SendAsync(ClipMessage.Clipboard(text, NextSeq()), ct);

    /// <summary>Sends a keepalive ping.</summary>
    public Task SendPingAsync(CancellationToken ct)
        => SendAsync(ClipMessage.Control(MessageType.Ping, NextSeq()), ct);

    private async Task SendAsync(ClipMessage msg, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ClipProtocol.WriteMessageAsync(_stream, _key, msg, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private long NextSeq() => Interlocked.Increment(ref _seq);

    public void Dispose() => _sendLock.Dispose();
}
