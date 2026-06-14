using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RDPClipGuard.ClipBridge;

/// <summary>Which role this instance plays in the bridge.</summary>
public enum BridgeMode
{
    /// <summary>Listen for an incoming connection (the LOCAL side).</summary>
    Server,
    /// <summary>Connect out to a server (the REMOTE side).</summary>
    Client
}

/// <summary>
/// ClipBridge configuration, persisted to <c>%AppData%\RDPClipGuard\bridge.json</c>. The password
/// is held in memory in plaintext but stored on disk DPAPI-encrypted (current user).
/// </summary>
public sealed class BridgeSettings
{
    public const int DefaultPort = 9512;

    public BridgeMode Mode { get; set; } = BridgeMode.Server;
    public string Host { get; set; } = "";          // client only
    public int Port { get; set; } = DefaultPort;
    public string Password { get; set; } = "";       // in-memory plaintext; never serialized directly
    public bool AutoConnect { get; set; }
    public int ReplayToleranceSeconds { get; set; } = 60;

    /// <summary>True when there is enough configuration to attempt a connection.</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(Password) &&
        Port is > 0 and <= 65535 &&
        (Mode == BridgeMode.Server || !string.IsNullOrWhiteSpace(Host));

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RDPClipGuard");

    private static string SettingsPath => Path.Combine(SettingsDir, "bridge.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Loads settings, or returns defaults if none exist / the file is unreadable.</summary>
    public static BridgeSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new BridgeSettings();

            var dto = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                      ?? new PersistedSettings();

            string password = "";
            if (!string.IsNullOrEmpty(dto.PasswordProtected))
            {
                try { password = DpapiProtector.Unprotect(dto.PasswordProtected); }
                catch { password = ""; } // unreadable on a different user/machine — start blank
            }

            return new BridgeSettings
            {
                Mode = dto.Mode,
                Host = dto.Host,
                Port = dto.Port,
                Password = password,
                AutoConnect = dto.AutoConnect,
                ReplayToleranceSeconds = dto.ReplayToleranceSeconds
            };
        }
        catch
        {
            return new BridgeSettings();
        }
    }

    /// <summary>Persists settings, encrypting the password with DPAPI.</summary>
    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);

        var dto = new PersistedSettings
        {
            Mode = Mode,
            Host = Host,
            Port = Port,
            PasswordProtected = string.IsNullOrEmpty(Password) ? null : DpapiProtector.Protect(Password),
            AutoConnect = AutoConnect,
            ReplayToleranceSeconds = ReplayToleranceSeconds
        };

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(dto, JsonOptions));
    }

    /// <summary>On-disk shape. The password is stored only as a DPAPI blob, never in plaintext.</summary>
    private sealed class PersistedSettings
    {
        public BridgeMode Mode { get; set; } = BridgeMode.Server;
        public string Host { get; set; } = "";
        public int Port { get; set; } = DefaultPort;
        public string? PasswordProtected { get; set; }
        public bool AutoConnect { get; set; }
        public int ReplayToleranceSeconds { get; set; } = 60;
    }
}
