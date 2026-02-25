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

    public event Action<string>? StatusChanged;
    public int CopyCount => _copyCount;
    public DateTime StartTime => _startTime;

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

    private void CheckClipboard(object? state)
    {
        try
        {
            string? current = GetClipboardText();

            if (current == null)
            {
                RaiseStatus($"Clipboard inaccessible after {_copyCount} copies - resetting rdpclip");
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

                RaiseStatus($"Copy #{_copyCount} | {elapsed:F1} min | \"{preview}\"");

                if (_copyCount % ResetAfterCopies == 0)
                {
                    RaiseStatus($"Scheduled reset after {_copyCount} copies");
                    ResetRdpClip();
                }
            }
        }
        catch (Exception ex)
        {
            RaiseStatus($"Error: {ex.Message}");
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

    private static void ResetRdpClip()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("rdpclip"))
            {
                try { proc.Kill(); } catch { }
            }

            Thread.Sleep(500);

            var psi = new ProcessStartInfo
            {
                FileName = "rdpclip.exe",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);

            Thread.Sleep(500);
        }
        catch
        {
            // Silently handle - will retry on next cycle
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
            _timer.Dispose();
        }
    }
}
