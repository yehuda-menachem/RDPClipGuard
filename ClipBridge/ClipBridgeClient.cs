using System.Net.Sockets;

namespace RDPClipGuard.ClipBridge;

/// <summary>
/// ClipBridge client (the REMOTE role): connects to a server, performs the client-side handshake,
/// and runs one session until the connection drops or is cancelled. Reconnection with backoff is
/// the caller's responsibility (see BridgeManager).
/// </summary>
public sealed class ClipBridgeClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _password;
    private readonly int _replayToleranceSeconds;

    private readonly object _sessionLock = new();
    private ClipBridgeSession? _session;
    private bool _disposed;

    /// <summary>Raised with clipboard text received from the server.</summary>
    public event Action<string>? RemoteClipboardReceived;
    /// <summary>Raised when the connection changes state (true = connected).</summary>
    public event Action<bool>? ConnectionChanged;
    /// <summary>Diagnostic/status messages.</summary>
    public event Action<string>? StatusLog;

    public ClipBridgeClient(string host, int port, string password, int replayToleranceSeconds)
    {
        _host = host;
        _port = port;
        _password = password;
        _replayToleranceSeconds = replayToleranceSeconds;
    }

    /// <summary>
    /// Connects, handshakes, and runs the session to completion. Throws on connect/handshake
    /// failure (the caller decides whether to retry). Returns when the session ends.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);

        NetworkStream stream = client.GetStream();
        byte[] key = await ClipHandshake.ClientAsync(stream, _password, ct).ConfigureAwait(false);

        StatusLog?.Invoke($"[CLIENT] Connected to {_host}:{_port}");
        var session = new ClipBridgeSession(stream, key, _replayToleranceSeconds);
        session.ClipboardReceived += OnSessionClipboard;
        lock (_sessionLock) { _session = session; }
        ConnectionChanged?.Invoke(true);

        try
        {
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            session.ClipboardReceived -= OnSessionClipboard;
            lock (_sessionLock) { if (ReferenceEquals(_session, session)) _session = null; }
            ConnectionChanged?.Invoke(false);
            session.Dispose();
        }
    }

    /// <summary>Sends clipboard text to the server, if connected.</summary>
    public async Task SendClipboardAsync(string text)
    {
        ClipBridgeSession? session;
        lock (_sessionLock) { session = _session; }
        if (session == null)
            return;

        try { await session.SendClipboardAsync(text, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { StatusLog?.Invoke($"[CLIENT] Send failed: {ex.GetType().Name}"); }
    }

    private void OnSessionClipboard(string text) => RemoteClipboardReceived?.Invoke(text);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sessionLock) { _session = null; }
    }
}
