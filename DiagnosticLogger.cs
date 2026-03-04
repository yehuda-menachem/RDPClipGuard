using System.Text;

namespace RDPClipGuard;

/// <summary>
/// Writes diagnostic logs to a file next to the EXE (portable, no install directory needed).
/// Thread-safe file writing with automatic rotation.
/// </summary>
public sealed class DiagnosticLogger : IDisposable
{
    private readonly string _logDirectory;
    private readonly string _logPathBase;
    private readonly object _lockObject = new object();
    private bool _disposed;
    private string _currentLogFile;
    private DateTime _sessionStartTime;

    public DiagnosticLogger(string? role = null)
    {
        // Try AppContext.BaseDirectory first (works with single-file publish)
        // If not writable, fall back to AppData
        _logDirectory = DetermineLogDirectory();
        _logPathBase = Path.Combine(_logDirectory, "RDPClipGuard_Diagnostics");
        _sessionStartTime = DateTime.Now;
        _currentLogFile = GetLogFilePath();

        // Auto-detect role if not provided
        var detectedRole = System.Windows.Forms.SystemInformation.TerminalServerSession ? "REMOTE" : "LOCAL";
        var displayRole = role ?? detectedRole;

        WriteInitialHeader(displayRole);
    }

    /// <summary>
    /// Determines the appropriate log directory, trying AppContext.BaseDirectory first,
    /// then falling back to AppData if not writable (e.g., when installed in Program Files).
    /// </summary>
    private string DetermineLogDirectory()
    {
        // Try the installation directory first
        string primaryDir = AppContext.BaseDirectory;
        if (IsDirectoryWritable(primaryDir))
        {
            return primaryDir;
        }

        // Fall back to AppData\RDPClipGuard if primary directory is not writable
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RDPClipGuard");

        // Create AppData directory if it doesn't exist
        if (!Directory.Exists(appDataDir))
        {
            try
            {
                Directory.CreateDirectory(appDataDir);
            }
            catch
            {
                // If AppData creation fails, try LocalAppData as last resort
                appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RDPClipGuard");
                Directory.CreateDirectory(appDataDir);
            }
        }

        return appDataDir;
    }

    /// <summary>
    /// Checks if a directory exists and is writable by attempting to create a test file.
    /// </summary>
    private bool IsDirectoryWritable(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
                return false;

            string testFilePath = Path.Combine(directoryPath, ".rdpclipguard_write_test");
            File.WriteAllText(testFilePath, "test");
            File.Delete(testFilePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the path to the current log file (creates new file each session).
    /// </summary>
    private string GetLogFilePath()
    {
        string timestamp = _sessionStartTime.ToString("yyyy-MM-dd_HH-mm-ss");
        string filename = $"{_logPathBase}_{timestamp}.log";
        return filename;
    }

    public string LogFilePath => _currentLogFile;
    public string LogDirectory => _logDirectory;

    /// <summary>
    /// Writes an entry to the log file with timestamp and optional metadata.
    /// Thread-safe.
    /// </summary>
    public void Log(string message, string? category = null)
    {
        if (_disposed)
            return;

        lock (_lockObject)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string categoryPrefix = string.IsNullOrEmpty(category) ? "" : $"[{category}] ";
                string logEntry = $"[{timestamp}] {categoryPrefix}{message}{Environment.NewLine}";

                File.AppendAllText(_currentLogFile, logEntry, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Log file might be in use by another process or accessed concurrently
                // Fail silently to avoid disrupting the main app
            }
            catch (UnauthorizedAccessException)
            {
                // No write permission to the directory
                // Fail silently
            }
            catch
            {
                // Any other exception - fail silently
            }
        }
    }

    /// <summary>
    /// Writes a separator line for visual organization.
    /// </summary>
    public void LogSeparator()
    {
        Log("─────────────────────────────────────────────────────────────────");
    }

    /// <summary>
    /// Writes a highlighted section header.
    /// </summary>
    public void LogHeader(string title)
    {
        LogSeparator();
        Log($"╔═ {title} ═══════════════════════════════════════════════════════");
        LogSeparator();
    }

    /// <summary>
    /// Writes a warning message.
    /// </summary>
    public void LogWarning(string message)
    {
        Log($"⚠️  WARNING: {message}", "WARN");
    }

    /// <summary>
    /// Writes an error message.
    /// </summary>
    public void LogError(string message)
    {
        Log($"❌ ERROR: {message}", "ERROR");
    }

    /// <summary>
    /// Writes an info message with checkmark.
    /// </summary>
    public void LogInfo(string message)
    {
        Log($"ℹ️  {message}", "INFO");
    }

    /// <summary>
    /// Writes a success message.
    /// </summary>
    public void LogSuccess(string message)
    {
        Log($"✅ {message}", "OK");
    }

    /// <summary>
    /// Writes a diagnostic point (used for clipboard/rdpclip events).
    /// </summary>
    public void LogDiagnostic(string message)
    {
        Log($"🔍 {message}", "DIAG");
    }

    /// <summary>
    /// Writes initial session header with system info.
    /// </summary>
    private void WriteInitialHeader(string role)
    {
        try
        {
            var isRdp = System.Windows.Forms.SystemInformation.TerminalServerSession;
            var os = Environment.OSVersion;
            var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

            LogHeader($"RDPClipGuard Diagnostic Session - [{role}]");
            Log($"Session Started: {_sessionStartTime:yyyy-MM-dd HH:mm:ss}");
            Log($"Log File: {_currentLogFile}");
            Log("");
            Log($"Role: {role} (RDP Session: {isRdp})");
            Log($"OS: {os.Platform} {os.VersionString}");
            Log($"Runtime: {runtime}");
            Log($"AppContext.BaseDirectory: {AppContext.BaseDirectory}");
            LogSeparator();
            Log("");
        }
        catch
        {
            // If initial write fails, continue anyway
        }
    }

    /// <summary>
    /// Gets the current session's uptime since this logger was created.
    /// </summary>
    public TimeSpan SessionUptime => DateTime.Now - _sessionStartTime;

    /// <summary>
    /// Cleans up old log files, keeping only the N most recent ones.
    /// </summary>
    public void CleanupOldLogs(int keepCount = 5)
    {
        try
        {
            var logDir = new DirectoryInfo(_logDirectory);
            var logFiles = logDir
                .GetFiles("RDPClipGuard_Diagnostics_*.log")
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            if (logFiles.Count > keepCount)
            {
                for (int i = keepCount; i < logFiles.Count; i++)
                {
                    try
                    {
                        logFiles[i].Delete();
                        Log($"Cleaned up old log: {logFiles[i].Name}");
                    }
                    catch
                    {
                        // Continue if deletion fails
                    }
                }
            }
        }
        catch
        {
            // Fail silently
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                LogSeparator();
                Log($"Session Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Log($"Total Duration: {SessionUptime.TotalSeconds:F1} seconds");
                LogSeparator();
            }
            catch
            {
                // Fail silently on disposal
            }
        }
    }
}
