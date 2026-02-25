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
        _mutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "RDPClipGuard is already running.\nCheck the system tray icons.",
                "RDPClipGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
        finally
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
