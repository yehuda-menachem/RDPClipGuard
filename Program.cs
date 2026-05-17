using System.Windows.Forms;

namespace RDPClipGuard;

static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        // Ensure only one instance runs at a time
        const string mutexName = "Global\\RDPClipGuard_SingleInstance";
        _mutex = new Mutex(false, mutexName, out bool createdNew);

        // If we created the mutex, we own it - no need to wait
        // If we didn't create it, try to acquire it with a timeout
        // First instance: mutex is unowned, WaitOne(0) returns immediately.
        // Subsequent instances: wait up to 3s for the running instance to exit.
        bool canRun = _mutex.WaitOne(createdNew ? 0 : 3000);

        if (!canRun)
        {
            MessageBox.Show(
                "RDPClipGuard is already running.\nCheck the system tray icons.",
                "RDPClipGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            _mutex?.Dispose();
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
        finally
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Mutex may have been abandoned if the app crashed
            }
            _mutex?.Dispose();
        }
    }
}
