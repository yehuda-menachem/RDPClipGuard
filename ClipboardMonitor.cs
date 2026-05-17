using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RDPClipGuard;

public sealed class ClipboardMonitor : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private string _lastContent = "";
    private int _copyCount;
    private readonly DateTime _startTime = DateTime.Now;
    private const int ResetAfterCopies = 7;
    private const int PollIntervalMs = 2000;
    private bool _disposed;

    // Diagnostic features
    private ClipboardListener? _clipboardListener;
    private DiagnosticLogger? _diagnosticLogger;
    private bool _diagnosticsEnabled;
    public event Action<string>? StatusChanged;
    public int CopyCount => _copyCount;
    public DateTime StartTime => _startTime;
    /// <summary>
    /// Gets whether diagnostic mode is currently enabled.
    /// </summary>
    public bool DiagnosticsEnabled => _diagnosticsEnabled;

    public ClipboardMonitor()
    {
        _timer = new System.Threading.Timer(CheckClipboard, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _timer.Change(0, PollIntervalMs);
        RaiseStatus("Monitoring started");
    }

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        RaiseStatus("Monitoring stopped");
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
        _clipboardListener = new ClipboardListener();
        _clipboardListener.ClipboardChanged += OnClipboardListenerChanged;
        _clipboardListener.Start();

        _diagnosticLogger.LogInfo("Event-based clipboard listener started");
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

        var textPreview = e.ClipboardText != null
            ? (e.ClipboardText.Length > 50 ? e.ClipboardText.Substring(0, 50) + "..." : e.ClipboardText)
            : "(empty or non-text)";

        // Check if there's a gap in sequence numbers (listener might have missed events)
        uint seqGap = e.CurrentSequenceNumber - e.PreviousSequenceNumber;
        string gapWarning = seqGap > 1 ? $" ⚠️ GAP={seqGap - 1} MISSED" : "";

        _diagnosticLogger.LogDiagnostic(
            $"Seq: {e.PreviousSequenceNumber}→{e.CurrentSequenceNumber} | " +
            $"Formats: {e.FormatList} | Hash: {e.TextHash ?? "N/A"} | " +
            $"Text: \"{textPreview}\"{gapWarning}");
    }

    private void CheckClipboard(object? state)
    {
        try
        {
            string? current = GetClipboardText();

            if (current == null)
            {
                var msg = $"Clipboard inaccessible after {_copyCount} copies - resetting rdpclip";
                RaiseStatus(msg);
                _diagnosticLogger?.LogWarning(msg);
                ResetRdpClip();
                return;
            }

            if (!string.IsNullOrEmpty(current) && current != _lastContent)
            {
                _copyCount++;
                _lastContent = current;

                var elapsed = (DateTime.Now - _startTime).TotalMinutes;
                var preview = current.Length > 50 ? current[..50] : current;
                preview = preview.Replace("\n", " ").Replace("\r", "");

                var status = $"Copy #{_copyCount} | {elapsed:F1} min | \"{preview}\"";
                RaiseStatus(status);

                // Log to diagnostics if enabled
                if (_diagnosticsEnabled)
                {
                    _diagnosticLogger?.LogDiagnostic($"{status}");

                    // Extra warning if this is a high copy count (clipboard getting stressed)
                    if (_copyCount > 100)
                    {
                        _diagnosticLogger?.LogWarning($"High copy count ({_copyCount}) - clipboard may be under stress");
                    }

                    _diagnosticLogger?.LogInfo(RdpClipHealth.GetDiagnosticSummary());
                }

                if (_copyCount % ResetAfterCopies == 0)
                {
                    var resetMsg = $"Scheduled reset after {_copyCount} copies";
                    RaiseStatus(resetMsg);
                    _diagnosticLogger?.LogInfo(resetMsg);
                    ResetRdpClip();
                }
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"Error: {ex.Message}";
            RaiseStatus(errorMsg);
            _diagnosticLogger?.LogError(errorMsg);
        }
    }

    private static string? GetClipboardText()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            return null;
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

    private void ResetRdpClip()
    {
        try
        {
            _diagnosticLogger?.LogInfo("Resetting rdpclip.exe...");

            foreach (var proc in Process.GetProcessesByName("rdpclip"))
            {
                try
                {
                    _diagnosticLogger?.LogDiagnostic($"Killing rdpclip PID={proc.Id}");
                    proc.Kill();
                }
                catch { }
                finally { proc.Dispose(); }
            }

            Thread.Sleep(500);

            var psi = new ProcessStartInfo
            {
                FileName = "rdpclip.exe",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var newProc = Process.Start(psi);
            if (newProc != null)
            {
                _diagnosticLogger?.LogSuccess($"rdpclip restarted: PID={newProc.Id}");
            }

            // Wait for rdpclip to be fully operational before resuming monitoring
            WaitForRdpClipReady(5000);
            _diagnosticLogger?.LogInfo(RdpClipHealth.GetDiagnosticSummary());
        }
        catch (Exception ex)
        {
            _diagnosticLogger?.LogError($"Failed to reset rdpclip: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits for rdpclip.exe to be fully operational (responsive AND clipboard accessible).
    /// </summary>
    private static void WaitForRdpClipReady(int timeoutMs)
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
                    if (NativeMethods.OpenClipboard(IntPtr.Zero))
                    {
                        NativeMethods.CloseClipboard();
                        ready = true;
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

