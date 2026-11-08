using Sdmm.Core.Config;
using Sdmm.Core.Installs;

namespace Sdmm.Remote;

/// <summary>Creates a <see cref="GameInstall"/> from saved settings. Dispose the result to close remote connections.</summary>
public static class InstallFactory
{
    /// <param name="unprotect">Decrypts a saved password (DPAPI).</param>
    /// <param name="confirmNewFingerprint">Asked once per server to accept its key or certificate.</param>
    /// <param name="fingerprintAccepted">Called with a newly accepted fingerprint so it can be saved in the config.</param>
    public static OpenedInstall Open(InstallConfig cfg, Func<string?, string?> unprotect,
        Func<string, bool> confirmNewFingerprint, Action<string>? fingerprintAccepted = null)
    {
        switch (cfg.Kind)
        {
            case InstallKind.Local:
                return new OpenedInstall(new GameInstall(cfg.Name, new LocalInstallFileSystem(cfg.Path), cfg.TargetOverride), null);

            case InstallKind.Sftp:
            {
                var trust = new HostKeyTrust(cfg.TrustedFingerprint, confirmNewFingerprint) { Accepted = fingerprintAccepted };
                var fs = new SftpInstallFileSystem(Require(cfg.Host, "host"), cfg.Port ?? 22, Require(cfg.User, "user name"),
                    unprotect(cfg.ProtectedPassword), cfg.PrivateKeyPath, cfg.Path, trust);
                return new OpenedInstall(new GameInstall(cfg.Name, fs, cfg.TargetOverride), fs);
            }

            case InstallKind.Ftp:
            {
                var trust = new HostKeyTrust(cfg.TrustedFingerprint, confirmNewFingerprint) { Accepted = fingerprintAccepted };
                var fs = new FtpInstallFileSystem(Require(cfg.Host, "host"), cfg.Port ?? 21, Require(cfg.User, "user name"),
                    unprotect(cfg.ProtectedPassword) ?? "", cfg.UseFtps, cfg.Path, trust);
                return new OpenedInstall(new GameInstall(cfg.Name, fs, cfg.TargetOverride), fs);
            }

            default:
                throw new NotSupportedException($"Unknown install kind {cfg.Kind}.");
        }
    }

    /// <summary>Connects and checks the remote root exists. No-op for local installs.</summary>
    public static void TestConnection(OpenedInstall opened)
    {
        switch (opened.Install.FileSystem)
        {
            case SftpInstallFileSystem s: s.TestConnection(); break;
            case FtpInstallFileSystem f: f.TestConnection(); break;
        }
    }

    private static string Require(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"The server install needs a {what}.") : value.Trim();
}

public sealed class OpenedInstall(GameInstall install, IDisposable? connection) : IDisposable
{
    public GameInstall Install { get; } = install;
    public void Dispose() => connection?.Dispose();
}
