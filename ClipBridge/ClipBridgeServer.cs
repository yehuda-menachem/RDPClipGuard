using System.Net;
using System.Net.Sockets;

namespace RDPClipGuard.ClipBridge;

/// <summary>
/// ClipBridge server (the LOCAL role): listens on a port and accepts connections in a loop, so a
/// dropped client can reconnect. A newly accepted connection replaces the previous active session.
/// </summary>
public sealed class ClipBridgeServer : IDisposable
{
    private readonly int _port;
    private readonly string _password;
    private readonly int _replayToleranceSeconds;

    private readonly object _sessionLock = new();
    private ClipBridgeSession? _session;
    private CancellationTokenSource? _sessionCts;

    private TcpListener? _listener;
    private bool _disposed;

    /// <summary>Raised with clipboard text received from the connected client.</summary>
    public event Action<string>? RemoteClipboardReceived;
    /// <summary>Raised when the active client connection changes (true = connected).</summary>
    public event Action<bool>? ConnectionChanged;
    /// <summary>Diagnostic/status messages.</summary>
    public event Action<string>? StatusLog;

    public ClipBridgeServer(int port, string password, int replayToleranceSeconds)
    {
        _port = port;
        _password = password;
        _replayToleranceSeconds = replayToleranceSeconds;
    }

    /// <summary>
    /// Binds the listener and accepts connections until cancelled. Each accepted connection is
    /// handled concurrently; the newest one becomes the active session.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        StatusLog?.Invoke($"[SERVER] Listening on port {_port}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                _ = HandleClientAsync(client, ct);
            }
        }
        finally
        {
            _listener.Stop();
            _listener = null;
            StatusLog?.Invoke("[SERVER] Stopped listening");
        }
    }

    /// <summary>Sends clipboard text to the connected client, if any.</summary>
    public async Task SendClipboardAsync(string text)
    {
        ClipBridgeSession? session;
        lock (_sessionLock) { session = _session; }
        if (session == null)
            return;

        try { await session.SendClipboardAsync(text, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { StatusLog?.Invoke($"[SERVER] Send failed: {ex.GetType().Name}"); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        client.NoDelay = true;

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        NetworkStream stream;
        byte[] key;
        try
        {
            stream = client.GetStream();
            key = await ClipHandshake.ServerAsync(stream, _password, _replayToleranceSeconds, sessionCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusLog?.Invoke($"[SERVER] Handshake rejected from {remote}: {ex.GetType().Name}");
            client.Dispose();
            return;
        }

        StatusLog?.Invoke($"[SERVER] Client connected: {remote}");
        var session = new ClipBridgeSession(stream, key, _replayToleranceSeconds);
        session.ClipboardReceived += OnSessionClipboard;

        // Make this the active session and cancel any previous one.
        CancellationTokenSource? previous;
        lock (_sessionLock)
        {
            previous = _sessionCts;
            _session = session;
            _sessionCts = sessionCts;
        }
        previous?.Cancel();
        ConnectionChanged?.Invoke(true);

        try
        {
            await session.RunAsync(sessionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* replaced or shutting down */ }
        catch (Exception ex) { StatusLog?.Invoke($"[SERVER] Session ended ({remote}): {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            session.ClipboardReceived -= OnSessionClipboard;

            bool wasActive;
            lock (_sessionLock)
            {
                wasActive = ReferenceEquals(_session, session);
                if (wasActive) { _session = null; _sessionCts = null; }
            }
            if (wasActive)
                ConnectionChanged?.Invoke(false);

            session.Dispose();
            client.Dispose();
        }
    }

    private void OnSessionClipboard(string text) => RemoteClipboardReceived?.Invoke(text);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancellationTokenSource? cts;
        lock (_sessionLock) { cts = _sessionCts; _sessionCts = null; _session = null; }
        cts?.Cancel();
        cts?.Dispose();

        try { _listener?.Stop(); } catch { /* ignore */ }
    }
}
