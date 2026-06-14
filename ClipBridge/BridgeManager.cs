namespace RDPClipGuard.ClipBridge;

/// <summary>Connection state surfaced to the UI.</summary>
public enum BridgeState { Disabled, Listening, Connecting, Connected, Disconnected, Error }

/// <summary>A status snapshot for the tray UI.</summary>
public sealed class BridgeStatus
{
    public BridgeState State { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Wires <see cref="ClipboardMonitor"/> to a <see cref="ClipBridgeServer"/> or
/// <see cref="ClipBridgeClient"/>: forwards local clipboard changes to the peer, applies remote
/// changes locally, and suppresses echoes by content hash so it is correct whether ClipBridge
/// replaces or coexists with rdpclip. Client connections auto-reconnect with exponential backoff.
/// </summary>
public sealed class BridgeManager : IDisposable
{
    private const int InitialBackoffSeconds = 2;
    private const int MaxBackoffSeconds = 60;

    private ClipboardMonitor? _monitor;

    // Anti-echo: hashes of the last value we sent to / applied from the peer.
    private readonly object _echoLock = new();
    private string? _lastSentHash;
    private string? _lastAppliedHash;

    private ClipBridgeServer? _server;
    private ClipBridgeClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile int _reconnectDelaySeconds = InitialBackoffSeconds;

    private BridgeState _state = BridgeState.Disabled;

    /// <summary>Raised on connection-state transitions for the tray UI.</summary>
    public event Action<BridgeStatus>? StatusChanged;
    /// <summary>Raised with verbose server/client diagnostics (optional).</summary>
    public event Action<string>? Log;

    public BridgeState State => _state;
    public bool IsRunning => _runTask is { IsCompleted: false };

    /// <summary>Subscribes to the monitor's always-on clipboard-change event.</summary>
    public void Attach(ClipboardMonitor monitor)
    {
        if (_monitor != null)
            _monitor.ClipboardTextChanged -= OnLocalClipboardChanged;
        _monitor = monitor;
        _monitor.ClipboardTextChanged += OnLocalClipboardChanged;
    }

    /// <summary>Starts the bridge per <paramref name="settings"/>, stopping any prior run first.</summary>
    public async Task StartAsync(BridgeSettings settings)
    {
        await StopAsync().ConfigureAwait(false);

        if (!settings.IsConfigured)
        {
            RaiseStatus(BridgeState.Disabled, "Not configured");
            return;
        }

        lock (_echoLock) { _lastSentHash = null; _lastAppliedHash = null; }
        _reconnectDelaySeconds = InitialBackoffSeconds;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        if (settings.Mode == BridgeMode.Server)
        {
            _server = new ClipBridgeServer(settings.Port, settings.Password, settings.ReplayToleranceSeconds);
            _server.RemoteClipboardReceived += OnRemoteClipboardReceived;
            _server.ConnectionChanged += OnServerConnectionChanged;
            _server.StatusLog += m => Log?.Invoke(m);
            RaiseStatus(BridgeState.Listening, $"Listening on port {settings.Port}");
            _runTask = RunServerAsync(ct);
        }
        else
        {
            _client = new ClipBridgeClient(settings.Host, settings.Port, settings.Password, settings.ReplayToleranceSeconds);
            _client.RemoteClipboardReceived += OnRemoteClipboardReceived;
            _client.ConnectionChanged += OnClientConnectionChanged;
            _client.StatusLog += m => Log?.Invoke(m);
            _runTask = RunClientWithReconnectAsync(settings, ct);
        }

        await Task.CompletedTask;
    }

    /// <summary>Stops the bridge and waits for the run loop to unwind.</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();

        if (_runTask != null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch { /* expected on cancellation */ }
            _runTask = null;
        }

        if (_server != null)
        {
            _server.RemoteClipboardReceived -= OnRemoteClipboardReceived;
            _server.ConnectionChanged -= OnServerConnectionChanged;
            _server.Dispose();
            _server = null;
        }
        if (_client != null)
        {
            _client.RemoteClipboardReceived -= OnRemoteClipboardReceived;
            _client.ConnectionChanged -= OnClientConnectionChanged;
            _client.Dispose();
            _client = null;
        }

        _cts?.Dispose();
        _cts = null;
        RaiseStatus(BridgeState.Disabled, "Stopped");
    }

    private async Task RunServerAsync(CancellationToken ct)
    {
        try
        {
            await _server!.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RaiseStatus(BridgeState.Error, $"Server error: {ex.Message}");
        }
    }

    private async Task RunClientWithReconnectAsync(BridgeSettings settings, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RaiseStatus(BridgeState.Connecting, $"Connecting to {settings.Host}:{settings.Port}…");
                await _client!.RunAsync(ct).ConfigureAwait(false);
                // Returned normally → the peer closed the connection.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RaiseStatus(BridgeState.Error, $"Connection failed: {ex.Message}");
            }

            if (ct.IsCancellationRequested)
                break;

            int delay = _reconnectDelaySeconds;
            RaiseStatus(BridgeState.Disconnected, $"Reconnecting in {delay}s…");
            try { await Task.Delay(delay * 1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            _reconnectDelaySeconds = Math.Min(delay * 2, MaxBackoffSeconds);
        }
    }

    // ---- clipboard plumbing ----

    private void OnLocalClipboardChanged(ClipboardTextChange change)
    {
        lock (_echoLock)
        {
            // Skip values we just applied from the peer or already sent (defense in depth — the
            // monitor also suppresses self-writes before they reach this event).
            if (change.Hash == _lastAppliedHash || change.Hash == _lastSentHash)
                return;
            _lastSentHash = change.Hash;
        }

        _ = SendToPeerAsync(change.Text);
    }

    private void OnRemoteClipboardReceived(string text)
    {
        if (_monitor == null || string.IsNullOrEmpty(text))
            return;

        string hash = ClipboardMonitor.HashText(text);
        lock (_echoLock)
        {
            if (hash == _lastSentHash)
                return; // our own value bounced back from the peer
            _lastAppliedHash = hash;
        }

        // SetClipboardText records a self-write internally, so the resulting change event is
        // suppressed and not echoed back.
        _monitor.SetClipboardText(text);
    }

    private async Task SendToPeerAsync(string text)
    {
        try
        {
            if (_server != null) await _server.SendClipboardAsync(text).ConfigureAwait(false);
            else if (_client != null) await _client.SendClipboardAsync(text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[BRIDGE] Send to peer failed: {ex.GetType().Name}");
        }
    }

    private void OnServerConnectionChanged(bool connected)
        => RaiseStatus(connected ? BridgeState.Connected : BridgeState.Listening,
                       connected ? "Client connected" : "Waiting for client");

    private void OnClientConnectionChanged(bool connected)
    {
        if (connected)
        {
            _reconnectDelaySeconds = InitialBackoffSeconds; // reset backoff on a good connection
            RaiseStatus(BridgeState.Connected, "Connected");
        }
        // Disconnect transition is handled by the reconnect loop.
    }

    private void RaiseStatus(BridgeState state, string message)
    {
        _state = state;
        StatusChanged?.Invoke(new BridgeStatus { State = state, Message = message });
    }

    public void Dispose()
    {
        if (_monitor != null)
            _monitor.ClipboardTextChanged -= OnLocalClipboardChanged;

        try { StopAsync().GetAwaiter().GetResult(); }
        catch { /* best effort */ }
    }
}
