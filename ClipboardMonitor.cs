using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RDPClipGuard;

/// <summary>
/// State of the clipboard monitoring system for diagnostics.
/// </summary>
public enum MonitorState
{
    Starting,                    // Initial state
    Monitoring_ListenerActive,   // Listener is receiving events
    Monitoring_ListenerQuiet,    // Listener registered but no events
    Recovering_ListenerStopped,  // Listener stopped, waiting to restart
    Recovering_RdpclipRestarting, // Stopping/starting rdpclip process
    Unhealthy_RepeatedRestarts   // Too many restarts, system unstable
}

public sealed class ClipboardMonitor : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private string _lastContent = "";
    private int _copyCount;
    private readonly DateTime _startTime = DateTime.Now;
    private const int ResetAfterCopies = 7;
    private const int PollIntervalMs = 2000;
    private bool _disposed;
    private bool _isShadowSession;
    private int _lastKnownRdpClipPid = -1;
    private DateTime _listenerLastEventTime = DateTime.MinValue;
    private int _healthCheckTick;
    private const int HealthCheckEveryTicks = 15; // every 30s at 2s poll interval
    private uint _lastPolledSequenceNumber;
    private int _checkInProgress; // 0 = idle, 1 = running — prevents re-entrant timer callbacks
    private readonly object _clipboardAccessLock = new(); // Synchronizes access between polling and listener

    // Diagnostic features
    private ClipboardListener? _clipboardListener;
    private DiagnosticLogger? _diagnosticLogger;
    private bool _diagnosticsEnabled;
    private int _listenerRegistrationFailures; // Track failed registration attempts

    // Fast silence detection
    private DateTime _lastRdpclipRestartTime = DateTime.MinValue;
    private int _consecutiveRestarts; // Count restarts in quick succession
    private DateTime _lastConsecutiveRestartTime = DateTime.MinValue;
    private const int FastSilenceDetectionThresholdSeconds = 5; // Detect silence in 5s instead of 2 minutes
    private const int RestartBackoffMs = 500; // Exponential backoff on successive restarts

    // State machine for diagnostics
    private MonitorState _currentState = MonitorState.Starting;
    private MonitorState _previousState = MonitorState.Starting;

    /// <summary>
    /// Gets the current state of the clipboard monitor.
    /// </summary>
    public MonitorState CurrentState => _currentState;
    public event Action<string>? StatusChanged;
    public int CopyCount => _copyCount;
    public DateTime StartTime => _startTime;
    /// <summary>
    /// Gets whether diagnostic mode is currently enabled.
    /// </summary>
    public bool DiagnosticsEnabled => _diagnosticsEnabled;

    /// <summary>
    /// Gets the path of the current diagnostic log file, or null if diagnostics are not enabled.
    /// </summary>
    public string? LogFilePath => _diagnosticLogger?.LogFilePath;

    /// <summary>
    /// Gets the directory where diagnostic log files are written, or null if diagnostics are not enabled.
    /// </summary>
    public string? LogDirectory => _diagnosticLogger?.LogDirectory;

    public ClipboardMonitor(bool isShadowSession = false)
    {
        _isShadowSession = isShadowSession;
        _lastPolledSequenceNumber = NativeMethods.GetClipboardSequenceNumber();
        _timer = new System.Threading.Timer(CheckClipboard, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _timer.Change(0, PollIntervalMs);
        TransitionState(MonitorState.Monitoring_ListenerActive, "start");
        RaiseStatus("Monitoring started");
    }

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        RaiseStatus("Monitoring stopped");
    }

    /// <summary>
    /// Transitions to a new state and logs the change.
    /// </summary>
    private void TransitionState(MonitorState newState, string reason)
    {
        if (_currentState == newState)
            return;

        _previousState = _currentState;
        _currentState = newState;

        _diagnosticLogger?.LogDiagnostic($"[STATE] {_previousState} → {newState} (reason: {reason})");
    }

    /// <summary>
    /// Updates the monitor state based on current conditions.
    /// </summary>
    private void UpdateMonitorState()
    {
        bool listenerActive = _clipboardListener?.IsListening == true;
        bool listenerHandleValid = _clipboardListener?.IsHandleValid == true;
        bool hasRecentEvent = _listenerLastEventTime != DateTime.MinValue &&
                             (DateTime.Now - _listenerLastEventTime).TotalSeconds < 10;
        bool repeatedRestarts = _consecutiveRestarts >= 3;

        if (repeatedRestarts)
        {
            TransitionState(MonitorState.Unhealthy_RepeatedRestarts, "too many restarts");
        }
        else if (!listenerHandleValid || !listenerActive)
        {
            TransitionState(MonitorState.Recovering_RdpclipRestarting, "listener not ready");
        }
        else if (hasRecentEvent)
        {
            TransitionState(MonitorState.Monitoring_ListenerActive, "receiving events");
        }
        else
        {
            TransitionState(MonitorState.Monitoring_ListenerQuiet, "listener quiet");
        }
    }

    /// <summary>
    /// Enables diagnostic logging mode.
    /// </summary>
    public void EnableDiagnostics(string? role = null)
    {
        if (_diagnosticsEnabled)
            return;

        _diagnosticsEnabled = true;
        _diagnosticLogger = new DiagnosticLogger(role);

        _diagnosticLogger.LogHeader("Diagnostics Enabled");
        _diagnosticLogger.LogInfo($"Clipboard polling every {PollIntervalMs}ms");
        _diagnosticLogger.LogInfo($"Auto-reset rdpclip every {ResetAfterCopies} copies");
        _diagnosticLogger.Log($"Current copy count: {_copyCount}");
        _diagnosticLogger.LogInfo(RdpClipHealth.GetDiagnosticSummary());

        // Set up event-based clipboard listener for real-time notification
        // Pass the shared lock so both polling and listener serialize their clipboard access
        _clipboardListener = new ClipboardListener(_clipboardAccessLock, _diagnosticLogger);
        _clipboardListener.ClipboardChanged += OnClipboardListenerChanged;

        try
        {
            _clipboardListener.Start();
            _diagnosticLogger.LogInfo("Event-based clipboard listener started");
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogError($"Failed to start clipboard listener: {ex.Message}");
        }

        _diagnosticLogger.Log("");

        RaiseStatus("Diagnostic mode enabled - logging to " + Path.GetFileName(_diagnosticLogger.LogFilePath));
    }

    /// <summary>
    /// Disables diagnostic logging mode.
    /// </summary>
    public void DisableDiagnostics()
    {
        if (!_diagnosticsEnabled)
            return;

        _diagnosticsEnabled = false;

        try
        {
            if (_clipboardListener != null)
            {
                _clipboardListener.ClipboardChanged -= OnClipboardListenerChanged;
                _clipboardListener.Dispose();
                _clipboardListener = null;
            }
        }
        finally
        {
            if (_diagnosticLogger != null)
            {
                _diagnosticLogger.LogInfo("Diagnostic mode disabled");
                _diagnosticLogger.Dispose();
                _diagnosticLogger = null;
            }
        }

        RaiseStatus("Diagnostic mode disabled");
    }

    /// <summary>
    /// Called when clipboard listener detects a change.
    /// </summary>
    private void OnClipboardListenerChanged(object? sender, ClipboardChangeEventArgs e)
    {
        if (_diagnosticLogger == null)
            return;

        _listenerLastEventTime = DateTime.Now;

        var textPreview = e.ClipboardText != null
            ? (e.ClipboardText.Length > 50 ? e.ClipboardText.Substring(0, 50) + "..." : e.ClipboardText)
            : "(empty or non-text)";

        uint seqGap = e.CurrentSequenceNumber - e.PreviousSequenceNumber;
        string gapWarning = seqGap > 1 ? $" ⚠️ GAP={seqGap - 1} MISSED" : "";

        _diagnosticLogger.LogDiagnostic(
            $"Seq: {e.PreviousSequenceNumber}→{e.CurrentSequenceNumber} | " +
            $"Formats: {e.FormatList} | Hash: {e.TextHash ?? "N/A"} | " +
            $"Text: \"{textPreview}\"{gapWarning}");

        if (seqGap > 20)
        {
            _diagnosticLogger.LogWarning($"Large sequence gap ({seqGap - 1} missed events) - checking rdpclip health");
            _diagnosticLogger.LogInfo(RdpClipHealth.GetDiagnosticSummary());
        }
    }

    private void CheckClipboard(object? state)
    {
        // Prevent re-entrant execution if a previous tick is still blocking (e.g. inside ResetRdpClip)
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
            return;

        try
        {
            // Cheap sequence-number check — skip clipboard open when nothing changed
            uint currentSeq = NativeMethods.GetClipboardSequenceNumber();
            if (currentSeq != _lastPolledSequenceNumber)
            {
                _lastPolledSequenceNumber = currentSeq;

                string? current = GetClipboardText();

                if (current == null)
                {
                    var msg = $"Clipboard inaccessible after {_copyCount} copies - resetting rdpclip";
                    RaiseStatus(msg);
                    _diagnosticLogger?.LogWarning(msg);
                    TransitionState(MonitorState.Recovering_RdpclipRestarting, "clipboard_inaccessible");
                    ResetRdpClip();
                }
                else if (!string.IsNullOrEmpty(current) && current != _lastContent)
                {
                    _copyCount++;
                    // Cap stored content to avoid keeping large documents in memory
                    _lastContent = current.Length > 4096 ? current[..4096] : current;

                    var elapsed = (DateTime.Now - _startTime).TotalMinutes;
                    var preview = current.Length > 50 ? current[..50] : current;
                    preview = preview.Replace("\n", " ").Replace("\r", "");

                    var status = $"Copy #{_copyCount} | {elapsed:F1} min | \"{preview}\"";
                    RaiseStatus(status);

                    if (_diagnosticsEnabled)
                    {
                        _diagnosticLogger?.LogPollingEvent($"{status}");

                        if (_copyCount > 100)
                            _diagnosticLogger?.LogWarning($"High copy count ({_copyCount}) - clipboard may be under stress");

                        _diagnosticLogger?.LogInfo(RdpClipHealth.GetDiagnosticSummary());
                    }

                    if (_copyCount % ResetAfterCopies == 0)
                    {
                        var resetMsg = $"Scheduled reset after {_copyCount} copies";
                        RaiseStatus(resetMsg);
                        _diagnosticLogger?.LogInfo(resetMsg);
                        TransitionState(MonitorState.Recovering_RdpclipRestarting, "scheduled_reset");
                        ResetRdpClip();
                    }
                }
            }

            // Periodic rdpclip + listener health check (every 30s); reset counter to prevent int overflow
            _healthCheckTick = (_healthCheckTick + 1) % HealthCheckEveryTicks;
            if (_healthCheckTick == 0)
                PerformHeartbeat();
        }
        catch (Exception ex)
        {
            var errorMsg = $"Error: {ex.Message}";
            RaiseStatus(errorMsg);
            _diagnosticLogger?.LogError(errorMsg);
        }
        finally
        {
            Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    private string? GetClipboardText()
    {
        // Acquire lock to serialize access between polling and listener
        if (!Monitor.TryEnter(_clipboardAccessLock, 5000)) // 5 second timeout
        {
            _diagnosticLogger?.LogWarning("[POLLING] Clipboard access lock timeout - clipboard may be busy");
            return null;
        }

        try
        {
            // Retry a few times with timeout protection — clipboard can be briefly locked by another process
            var startTime = DateTime.UtcNow;
            const int TimeoutMs = 500;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if ((DateTime.UtcNow - startTime).TotalMilliseconds > TimeoutMs)
                {
                    _diagnosticLogger?.LogWarning($"[POLLING] GetClipboardText timeout after {attempt} attempts");
                    return null;
                }

                if (attempt > 0) Thread.Sleep(50);

                if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                    continue;
                try
                {
                    IntPtr hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                    if (hData == IntPtr.Zero)
                        return "";
                    IntPtr ptr = NativeMethods.GlobalLock(hData);
                    if (ptr == IntPtr.Zero)
                        return null;
                    try { return Marshal.PtrToStringUni(ptr) ?? ""; }
                    finally { NativeMethods.GlobalUnlock(hData); }
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }
            return null;
        }
        finally
        {
            Monitor.Exit(_clipboardAccessLock);
        }
    }

    private void ResetRdpClip()
    {
        try
        {
            // Track consecutive restarts and apply exponential backoff
            var now = DateTime.Now;
            if (_lastConsecutiveRestartTime != DateTime.MinValue && (now - _lastConsecutiveRestartTime).TotalSeconds < 60)
            {
                _consecutiveRestarts++;
            }
            else
            {
                _consecutiveRestarts = 1;
            }
            _lastConsecutiveRestartTime = now;

            // If 3+ restarts in 60 seconds, log warning and skip this restart (system is unstable)
            if (_consecutiveRestarts >= 3)
            {
                _diagnosticLogger?.LogWarning($"[RDPCLIP] ⚠️ {_consecutiveRestarts} restarts in 60s - system may be unstable, delaying reset");
                RaiseStatus($"Clipboard system unstable ({_consecutiveRestarts} restarts). Waiting before retry...");

                // Add backoff delay proportional to restart count
                int backoffMs = Math.Min((_consecutiveRestarts - 2) * RestartBackoffMs, 3000);
                Thread.Sleep(backoffMs);

                if (_consecutiveRestarts >= 5)
                {
                    _diagnosticLogger?.LogError($"[RDPCLIP] Too many consecutive restarts - giving up for now");
                    return;
                }
            }

            _diagnosticLogger?.LogInfo($"[RDPCLIP] Resetting rdpclip.exe... (attempt {_consecutiveRestarts})");

            // Pause listener to avoid spurious events during the transition
            try
            {
                _clipboardListener?.Stop();
                _diagnosticLogger?.LogDiagnostic($"[RDPCLIP] Listener stopped");
            }
            catch (Exception ex)
            {
                _diagnosticLogger?.LogError($"[RDPCLIP] Failed to stop listener: {ex.Message}");
            }

            foreach (var proc in Process.GetProcessesByName("rdpclip"))
            {
                try
                {
                    _diagnosticLogger?.LogDiagnostic($"[RDPCLIP] Killing rdpclip PID={proc.Id}");
                    proc.Kill();
                }
                catch (Exception ex)
                {
                    _diagnosticLogger?.LogWarning($"[RDPCLIP] Failed to kill PID={proc.Id}: {ex.Message}");
                }
                finally { proc.Dispose(); }
            }

            Thread.Sleep(500);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "rdpclip.exe",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var newProc = Process.Start(psi);
                if (newProc != null)
                {
                    _diagnosticLogger?.LogSuccess($"[RDPCLIP] rdpclip restarted: PID={newProc.Id}");
                    _lastRdpclipRestartTime = DateTime.Now;
                }
                else
                    _diagnosticLogger?.LogWarning($"[RDPCLIP] Process.Start returned null");
            }
            catch (Exception ex)
            {
                _diagnosticLogger?.LogError($"[RDPCLIP] Failed to start rdpclip.exe: {ex.Message}");
            }

            WaitForRdpClipReady(5000);

            // Re-register listener after rdpclip is ready
            try
            {
                _clipboardListener?.Start();
                if (_clipboardListener?.IsListening == true)
                {
                    _diagnosticLogger?.LogSuccess($"[RDPCLIP] Listener re-registered successfully");
                    _listenerRegistrationFailures = 0;
                }
                else
                {
                    _listenerRegistrationFailures++;
                    _diagnosticLogger?.LogWarning($"[RDPCLIP] Listener failed to register (attempt {_listenerRegistrationFailures})");
                }
            }
            catch (Exception ex)
            {
                _listenerRegistrationFailures++;
                _diagnosticLogger?.LogError($"[RDPCLIP] Failed to start listener: {ex.Message}");
            }

            // Update tracked PID so heartbeat doesn't treat this as an unexpected change
            var newHealth = RdpClipHealth.GetHealth();
            if (newHealth.IsRunning)
                _lastKnownRdpClipPid = newHealth.ProcessId;

            _diagnosticLogger?.LogInfo(RdpClipHealth.GetDiagnosticSummary());
        }
        catch (Exception ex)
        {
            _diagnosticLogger?.LogError($"[RDPCLIP] Unexpected error in ResetRdpClip: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits for rdpclip.exe to be fully operational (responsive AND clipboard accessible).
    /// Acquires _clipboardAccessLock before each clipboard probe to serialize with polling.
    /// </summary>
    private void WaitForRdpClipReady(int timeoutMs)
    {
        var startTime = DateTime.UtcNow;
        while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
        {
            var processes = Process.GetProcessesByName("rdpclip");
            bool ready = false;
            try
            {
                if (processes.Length > 0 && processes[0].Responding)
                {
                    if (Monitor.TryEnter(_clipboardAccessLock, 200))
                    {
                        try
                        {
                            if (NativeMethods.OpenClipboard(IntPtr.Zero))
                            {
                                NativeMethods.CloseClipboard();
                                ready = true;
                            }
                        }
                        finally { Monitor.Exit(_clipboardAccessLock); }
                    }
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
            if (ready) return;
            Thread.Sleep(100);
        }
    }

    private void PerformHeartbeat()
    {
        var health = RdpClipHealth.GetHealth();

        // Update state based on current conditions
        UpdateMonitorState();

        if (_diagnosticsEnabled && _diagnosticLogger != null)
        {
            string listenerStatus = _clipboardListener?.IsListening == true ? "active" : "not registered";
            string handleValid = _clipboardListener?.IsHandleValid == true ? "valid" : "invalid";
            string lastEventInfo = _listenerLastEventTime == DateTime.MinValue
                ? "no events yet"
                : $"last event {(DateTime.Now - _listenerLastEventTime).TotalSeconds:F0}s ago";
            _diagnosticLogger.LogDiagnostic($"[HEARTBEAT] State={_currentState} | {RdpClipHealth.GetDiagnosticSummary()} | Listener: {listenerStatus} (handle={handleValid}, {lastEventInfo})");

            // Warn if listener registration has failed multiple times
            if (_listenerRegistrationFailures > 0)
            {
                _diagnosticLogger.LogWarning($"[HEARTBEAT] Listener registration failures: {_listenerRegistrationFailures}");
            }
        }

        // Check if listener window handle became invalid
        if (_clipboardListener?.IsListening == true && _clipboardListener.IsHandleValid == false)
        {
            _diagnosticLogger?.LogWarning($"[LISTENER] Window handle became invalid - re-registering listener");
            try
            {
                _clipboardListener.Stop();
                _clipboardListener.Start();
            }
            catch (Exception ex)
            {
                _diagnosticLogger?.LogError($"[LISTENER] Failed to re-register after handle invalidation: {ex.Message}");
            }
        }

        if (health.IsRunning)
        {
            if (_lastKnownRdpClipPid != -1 && _lastKnownRdpClipPid != health.ProcessId)
            {
                _diagnosticLogger?.LogWarning($"[RDPCLIP] PID changed {_lastKnownRdpClipPid} → {health.ProcessId} (external restart detected) - re-registering listener");
                try
                {
                    _clipboardListener?.Stop();
                    _clipboardListener?.Start();
                }
                catch (Exception ex)
                {
                    _diagnosticLogger?.LogError($"[RDPCLIP] Failed to re-register listener after PID change: {ex.Message}");
                }
            }
            _lastKnownRdpClipPid = health.ProcessId;
        }
        else
        {
            if (_lastKnownRdpClipPid != -1)
                _diagnosticLogger?.LogWarning($"[RDPCLIP] Process (was PID={_lastKnownRdpClipPid}) is no longer running - clipboard sync broken, restarting");
            else
                _diagnosticLogger?.LogWarning("[RDPCLIP] Process is not running - restarting");

            _lastKnownRdpClipPid = -1;
            _listenerLastEventTime = DateTime.Now; // prevent the quiet-listener check below from also resetting
            _lastRdpclipRestartTime = DateTime.MinValue; // prevent fast-silence false positive after a "not running" restart
            ResetRdpClip();
        }

        // Fast silence detection: if listener is quiet shortly after restart, it's broken
        if (_lastRdpclipRestartTime != DateTime.MinValue && _listenerLastEventTime != DateTime.MinValue)
        {
            var sinceRestart = DateTime.Now - _lastRdpclipRestartTime;
            var sinceLastEvent = DateTime.Now - _listenerLastEventTime;

            // If we restarted rdpclip but listener is still quiet 5s later, listener is broken
            if (sinceRestart.TotalSeconds < 30 && sinceLastEvent.TotalSeconds > FastSilenceDetectionThresholdSeconds)
            {
                _diagnosticLogger?.LogWarning($"[LISTENER] Fast silence detected: {sinceLastEvent.TotalSeconds:F0}s since restart - listener may be broken");
                _listenerLastEventTime = DateTime.Now;
                ResetRdpClip();
                return;
            }
        }

        // If listener went quiet for >2 minutes, rdpclip sync is likely broken — reset it
        if (_listenerLastEventTime != DateTime.MinValue)
        {
            var sinceLastEvent = DateTime.Now - _listenerLastEventTime;
            if (sinceLastEvent.TotalMinutes > 2)
            {
                var msg = $"No clipboard events for {sinceLastEvent.TotalMinutes:F1} min - resetting rdpclip to restore sync";
                _diagnosticLogger?.LogWarning(msg);
                RaiseStatus(msg);
                _listenerLastEventTime = DateTime.Now;
                ResetRdpClip();
            }
        }
    }

    private void RaiseStatus(string message)
    {
        StatusChanged?.Invoke(message);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            if (_diagnosticsEnabled)
            {
                DisableDiagnostics();
            }

            _timer.Dispose();
        }
    }
}

