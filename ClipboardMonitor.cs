using System.Diagnostics;
using System.Windows.Forms;

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

        if (_clipboardListener != null)
        {
            _clipboardListener.ClipboardChanged -= OnClipboardListenerChanged;
            _clipboardListener.Dispose();
            _clipboardListener = null;
        }

        if (_diagnosticLogger != null)
        {
            _diagnosticLogger.LogInfo("Diagnostic mode disabled");
            _diagnosticLogger.Dispose();
            _diagnosticLogger = null;
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

        _diagnosticLogger.LogDiagnostic(
            $"Seq: {e.PreviousSequenceNumber}→{e.CurrentSequenceNumber} | " +
            $"Formats: {e.FormatList} | Hash: {e.TextHash ?? "N/A"} | " +
            $"Text: \"{textPreview}\"");
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
        string? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsText())
                {
                    result = System.Windows.Forms.Clipboard.GetText();
                }
                else
                {
                    result = "";
                }
            }
            catch
            {
                result = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2000);
        return result;
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

            Thread.Sleep(500);
            _diagnosticLogger?.LogInfo(RdpClipHealth.GetDiagnosticSummary());
        }
        catch (Exception ex)
        {
            _diagnosticLogger?.LogError($"Failed to reset rdpclip: {ex.Message}");
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

