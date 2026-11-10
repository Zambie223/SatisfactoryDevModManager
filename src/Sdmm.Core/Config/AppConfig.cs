using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sdmm.Core.Discovery;
using Sdmm.Core.Json;

namespace Sdmm.Core.Config;

public enum InstallKind { Local, Sftp, Ftp }

public sealed class InstallConfig
{
    public string Name { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<InstallKind>))]
    public InstallKind Kind { get; set; } = InstallKind.Local;
    /// <summary>Local folder (Kind = Local) or remote game root (Sftp/Ftp).</summary>
    public string Path { get; set; } = "";
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? User { get; set; }
    /// <summary>DPAPI-protected, base64. Only readable by the same Windows user on the same PC.</summary>
    public string? ProtectedPassword { get; set; }
    /// <summary>SFTP only: private key file instead of (or with) a password.</summary>
    public string? PrivateKeyPath { get; set; }
    /// <summary>FTP only: use explicit FTPS (TLS). Plain FTP sends the password unencrypted.</summary>
    public bool UseFtps { get; set; } = true;
    /// <summary>
    /// SSH host key (SHA-256) or TLS certificate thumbprint accepted on first connect. A different value
    /// later is refused, because it may mean someone is intercepting the connection.
    /// </summary>
    public string? TrustedFingerprint { get; set; }
    /// <summary>
    /// Games only: command-line switches passed to the game exe by Play (for example "-DX12 -log"), like a game's
    /// launch options in Steam. Steam's own launch options don't apply, because Play starts the install's exe directly.
    /// </summary>
    public string? LaunchOptions { get; set; }
    /// <summary>
    /// The modpack this game or server follows (by name), like SMM's selected profile: SDMM's own changes here update
    /// it, and other differences are pointed out. Null for none.
    /// </summary>
    public string? ActiveModpack { get; set; }
    /// <summary>Platform to assume when the host does not expose Engine/Binaries (see GameInstall.TargetOverride).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<Packaging.ModTarget>))]
    public Packaging.ModTarget? TargetOverride { get; set; }

    [JsonIgnore]
    public bool IsRemote => Kind != InstallKind.Local;

    /// <summary>
    /// Identifies this install across launches (for "remember the last selection"): its kind, connection and folder.
    /// The display name isn't part of it, so renaming a server keeps it selected.
    /// </summary>
    [JsonIgnore]
    public string Key => IsRemote
        ? $"{Kind}|{Host?.Trim().ToLowerInvariant()}|{Port}|{User?.Trim()}|{Path.Trim().TrimEnd('/')}"
        : $"Local|{System.IO.Path.TrimEndingDirectorySeparator(Path.Trim()).ToLowerInvariant()}";
}

/// <summary>
/// Settings, stored as sdmm.config.json in <see cref="DataDirectory"/> (%LOCALAPPDATA%\SatisfactoryDevModManager),
/// wherever the exe is. The exe itself stays a single portable file; moving or re-downloading it keeps the settings.
/// </summary>
public sealed class AppConfig
{
    public const string FileName = "sdmm.config.json";

    public int Schema { get; set; } = 1;
    public SourceConfig Sources { get; set; } = new();
    public List<InstallConfig> Installs { get; set; } = [];
    /// <summary>Download cache. Null means a "cache" folder in <see cref="DataDirectory"/>.</summary>
    public string? CacheDirectory { get; set; }
    public bool IncludePrereleases { get; set; } = true;
    /// <summary>DPAPI-protected GitHub token for reading releases.</summary>
    public string? ProtectedReadToken { get; set; }
    public string? SmmDirectory { get; set; }
    /// <summary>True when the user chose "Don't ask again" on the startup GitHub token prompt.</summary>
    public bool TokenPromptDismissed { get; set; }
    /// <summary>Look mods up on SMR (ficsit.app) to show their icon, description and stats.</summary>
    public bool UseSmr { get; set; } = true;
    /// <summary>Check GitHub for a newer SDMM when the app starts.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    /// <summary>Release tag the user chose to skip; the startup check won't prompt for it again.</summary>
    public string? SkippedUpdateTag { get; set; }
    /// <summary>The game install selected when SDMM last closed (<see cref="InstallConfig.Key"/>), selected again at startup.</summary>
    public string? LastGameInstall { get; set; }
    /// <summary>The server selected when SDMM last closed (<see cref="InstallConfig.Key"/>).</summary>
    public string? LastServerInstall { get; set; }
    /// <summary>Saved modpacks.</summary>
    public List<Modpacks.Modpack> Modpacks { get; set; } = [];
    /// <summary>True once game installs were auto-detected on first launch, so removed installs aren't added back.</summary>
    public bool InstallsAutoDetected { get; set; }
    /// <summary>Check mod files with VirusTotal before installing them the first time (Settings → Security). Off by default.</summary>
    public bool VirusTotalEnabled { get; set; }
    /// <summary>Upload files VirusTotal hasn't seen. Off by default: uploaded files can be downloaded by VirusTotal's premium customers.</summary>
    public bool VirusTotalUpload { get; set; }
    /// <summary>The user's own VirusTotal API key, DPAPI-protected like the GitHub token.</summary>
    public string? ProtectedVirusTotalKey { get; set; }
    /// <summary>Write the detailed log (sdmm.log next to this file). Off by default; crash.log is written regardless.</summary>
    public bool VerboseLogging { get; set; }
    /// <summary>Branding variant (see Brand in the app).</summary>
    public bool Variant { get; set; }

    /// <summary>Where settings and the default download cache live: %LOCALAPPDATA%\SatisfactoryDevModManager.</summary>
    public static string DataDirectory { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SatisfactoryDevModManager");

    public static string DefaultPath => System.IO.Path.Combine(DataDirectory, FileName);

    /// <summary>The download cache: <see cref="CacheDirectory"/> if set (for example on a second drive), else DataDirectory\cache.</summary>
    public string ResolveCacheDirectory() =>
        string.IsNullOrWhiteSpace(CacheDirectory) ? System.IO.Path.Combine(DataDirectory, "cache") : CacheDirectory;

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppConfig();
        return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.AppConfig) ?? new AppConfig();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, CoreJsonContext.Default.AppConfig));
        File.Move(tmp, path, overwrite: true);
        Diagnostics.FileLog.Write("config", $"saved settings to {path}");
    }
}

/// <summary>Encrypts secrets with Windows DPAPI for the current user.</summary>
[SupportedOSPlatform("windows")]
public static class SecretProtector
{
    // Changing this makes every saved token and password unreadable (they then show as "not saved").
    private static readonly byte[] Entropy = "SDMM.v1"u8.ToArray();

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Returns null if the value is missing or was protected by another user or PC.</summary>
    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
