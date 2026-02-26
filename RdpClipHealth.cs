using System.Diagnostics;

namespace RDPClipGuard;

/// <summary>
/// Monitors the health and status of rdpclip.exe process.
/// Detects resource leaks, hanging, and responsiveness issues.
/// </summary>
public sealed class RdpClipHealth
{
    /// <summary>
    /// Information about rdpclip.exe health status.
    /// </summary>
    public class ProcessHealthInfo
    {
        public bool IsRunning { get; set; }
        public bool IsResponding { get; set; }
        public int ProcessId { get; set; }
        public DateTime StartTime { get; set; }
        public long WorkingSetMb { get; set; }
        public long PrivateMemoryMb { get; set; }
        public int HandleCount { get; set; }
        public TimeSpan UserProcessorTime { get; set; }
        public TimeSpan PrivilegedProcessorTime { get; set; }
        public int ThreadCount { get; set; }
        public TimeSpan Uptime { get; set; }

        /// <summary>
        /// Returns true if handle count indicates a resource leak (>1000 is suspicious).
        /// </summary>
        public bool HasSuspiciousHandleCount => HandleCount > 1000;

        /// <summary>
        /// Returns true if memory usage is high (>50MB is suspicious for rdpclip).
        /// </summary>
        public bool HasHighMemory => WorkingSetMb > 50;

        /// <summary>
        /// Returns true if not responding or not running.
        /// </summary>
        public bool IsUnhealthy => !IsRunning || !IsResponding;

        public override string ToString()
        {
            if (!IsRunning)
                return "NOT RUNNING";

            var parts = new List<string>
            {
                $"PID={ProcessId}",
                $"Uptime={Uptime.TotalMinutes:F1}min",
                $"Memory={WorkingSetMb}MB",
                $"Handles={HandleCount}",
                IsResponding ? "✓ Responding" : "✗ NOT RESPONDING"
            };

            if (HasSuspiciousHandleCount)
                parts.Add("⚠️ HANDLE LEAK?");
            if (HasHighMemory)
                parts.Add("⚠️ HIGH MEMORY");

            return string.Join(" | ", parts);
        }
    }

    /// <summary>
    /// Gets current health status of rdpclip.exe process.
    /// </summary>
    public static ProcessHealthInfo GetHealth()
    {
        var info = new ProcessHealthInfo();

        try
        {
            var processes = Process.GetProcessesByName("rdpclip");

            if (processes.Length == 0)
            {
                info.IsRunning = false;
                return info;
            }

            var proc = processes[0];

            try
            {
                proc.Refresh();

                info.IsRunning = !proc.HasExited;
                info.IsResponding = proc.Responding;
                info.ProcessId = proc.Id;
                info.StartTime = proc.StartTime;
                info.WorkingSetMb = proc.WorkingSet64 / (1024 * 1024);
                info.PrivateMemoryMb = proc.PrivateMemorySize64 / (1024 * 1024);
                info.HandleCount = proc.HandleCount;
                info.UserProcessorTime = proc.UserProcessorTime;
                info.PrivilegedProcessorTime = proc.PrivilegedProcessorTime;
                info.ThreadCount = proc.Threads.Count;
                info.Uptime = DateTime.Now - proc.StartTime;
            }
            catch
            {
                // Process may have exited between GetProcessesByName and refresh
                info.IsRunning = false;
            }
            finally
            {
                proc.Dispose();
            }
        }
        catch
        {
            // Any other error means process is not accessible
            info.IsRunning = false;
        }

        return info;
    }

    /// <summary>
    /// Checks if rdpclip needs a restart based on health indicators.
    /// </summary>
    public static bool NeedsRestart(ProcessHealthInfo health)
    {
        // Not running -> yes, restart
        if (!health.IsRunning)
            return true;

        // Not responding -> yes, restart
        if (!health.IsResponding)
            return true;

        // Handle count too high -> yes, restart (resource leak)
        if (health.HasSuspiciousHandleCount)
            return true;

        // Memory too high -> yes, restart
        if (health.HasHighMemory)
            return true;

        return false;
    }

    /// <summary>
    /// Gets a diagnostic summary of rdpclip health for logging.
    /// </summary>
    public static string GetDiagnosticSummary()
    {
        var health = GetHealth();

        if (!health.IsRunning)
            return "rdpclip: NOT RUNNING ❌";

        var status = health.IsResponding ? "✓" : "✗";
        return $"rdpclip: PID={health.ProcessId} | {health.WorkingSetMb}MB | " +
               $"Handles={health.HandleCount} | {status} Responding | " +
               $"Uptime={health.Uptime.TotalSeconds:F0}s";
    }
}
