using System.Windows;
using System.Windows.Controls;
using Sdmm.App.ViewModels;
using Sdmm.Core.Config;
using Sdmm.Core.Packaging;
using Microsoft.Win32;

namespace Sdmm.App;

/// <summary>Settings for a hosted server (SFTP/FTP), with a connection test before saving.</summary>
public partial class ServerWindow : Window
{
    private readonly InstallConfig _config;
    private readonly Func<InstallConfig, string?, Task<ServerTestResult>> _test;
    private bool _forgetKey;
    /// <summary>A key accepted during "Test connection", and the connection settings it belongs to.</summary>
    private (string Fingerprint, InstallKind Kind, string? Host, int? Port)? _testedKey;

    /// <summary>The newly typed password, or null to keep the saved one.</summary>
    public string? NewPassword { get; private set; }

    public ServerWindow(InstallConfig config, Func<InstallConfig, string?, Task<ServerTestResult>> test)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        _config = config;
        _test = test;
        NameBox.Text = config.Name;
        (config.Kind == InstallKind.Ftp ? FtpRadio : SftpRadio).IsChecked = true;
        FtpsBox.IsChecked = config.UseFtps;
        HostBox.Text = config.Host ?? "";
        PortBox.Text = config.Port?.ToString() ?? "";
        UserBox.Text = config.User ?? "";
        KeyBox.Text = config.PrivateKeyPath ?? "";
        PathBox.Text = string.IsNullOrEmpty(config.Path) ? "/" : config.Path;
        PasswordHint.Text = config.ProtectedPassword is null ? "Not saved." : "Saved (encrypted). Leave empty to keep it.";
        FingerprintBox.Text = config.TrustedFingerprint ?? "(none yet; you'll be asked on first connect)";
        TargetBox.SelectedIndex = config.TargetOverride switch
        {
            ModTarget.LinuxServer => 1,
            ModTarget.WindowsServer => 2,
            _ => 0,
        };
        OnProtocolChanged(this, new RoutedEventArgs());
    }

    private void OnProtocolChanged(object sender, RoutedEventArgs e)
    {
        if (FtpsBox is null) return; // fires during InitializeComponent
        bool ftp = FtpRadio.IsChecked == true;
        FtpsBox.IsEnabled = ftp;
        KeyBox.IsEnabled = !ftp;
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Private key file", CheckFileExists = true };
        if (d.ShowDialog(this) == true) KeyBox.Text = d.FileName;
    }

    private void OnForgetKey(object sender, RoutedEventArgs e)
    {
        _forgetKey = true;
        _testedKey = null;
        FingerprintBox.Text = "(will be asked again on next connect)";
    }

    private void Warn(string message) => MessageBox.Show(this, message, "Server settings", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Reads the fields into a new config. Returns null (after saying why) if they're incomplete.</summary>
    private InstallConfig? ReadFields(bool forTest)
    {
        int? port = null;
        if (PortBox.Text.Trim().Length > 0)
        {
            if (!int.TryParse(PortBox.Text.Trim(), out var p) || p is < 1 or > 65535)
            {
                Warn("Port must be a number from 1 to 65535.");
                return null;
            }
            port = p;
        }
        if (string.IsNullOrWhiteSpace(HostBox.Text) || string.IsNullOrWhiteSpace(UserBox.Text) || (!forTest && string.IsNullOrWhiteSpace(NameBox.Text)))
        {
            Warn(forTest ? "Host and user name are needed to connect." : "Name, host and user name are required.");
            return null;
        }
        bool ftp = FtpRadio.IsChecked == true;
        var kind = ftp ? InstallKind.Ftp : InstallKind.Sftp;
        var host = HostBox.Text.Trim();
        bool sameEndpoint = kind == _config.Kind && string.Equals(host, _config.Host, StringComparison.OrdinalIgnoreCase) && port == _config.Port;
        return new InstallConfig
        {
            Name = NameBox.Text.Trim(),
            Kind = kind,
            UseFtps = FtpsBox.IsChecked == true,
            Host = host,
            Port = port,
            User = UserBox.Text.Trim(),
            PrivateKeyPath = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim(),
            Path = string.IsNullOrWhiteSpace(PathBox.Text) ? "/" : PathBox.Text.Trim(),
            ProtectedPassword = _config.ProtectedPassword,
            // A different host or protocol means a different key; never carry the old fingerprint over.
            TrustedFingerprint = _forgetKey || !sameEndpoint ? null : _config.TrustedFingerprint,
            TargetOverride = ((TargetBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
            {
                "LinuxServer" => ModTarget.LinuxServer,
                "WindowsServer" => ModTarget.WindowsServer,
                _ => null,
            },
        };
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        try
        {
            // Runs again once if the user accepts a game folder found below the typed one.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var draft = ReadFields(forTest: true);
                if (draft is null) return;
                if (_testedKey is { } k && k.Kind == draft.Kind && k.Host == draft.Host && k.Port == draft.Port)
                    draft.TrustedFingerprint = k.Fingerprint;
                TestResult.Foreground = (System.Windows.Media.Brush)FindResource("SubtleTextBrush");
                TestResult.Text = "Connecting…";

                var result = await _test(draft, PasswordBox.Password.Length > 0 ? PasswordBox.Password : null);
                if (result.AcceptedFingerprint is { } fp)
                {
                    _testedKey = (fp, draft.Kind, draft.Host, draft.Port);
                    FingerprintBox.Text = fp;
                }
                TestResult.Foreground = (System.Windows.Media.Brush)FindResource(result.Ok ? "OkBrush" : "WarnBrush");
                TestResult.Text = result.Message;
                if (result.SuggestedPath is not { } path ||
                    MessageBox.Show(this, $"The game folder (with FactoryGame in it) is at:\n{path}\n\nUse that as the Game folder?",
                        "Game folder found", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                PathBox.Text = path;
            }
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var draft = ReadFields(forTest: false);
        if (draft is null) return;
        if (draft.Kind == InstallKind.Ftp && !draft.UseFtps &&
            MessageBox.Show(this, "Plain FTP sends your password and files unencrypted. Use it only if the host offers nothing else.\n\nContinue without TLS?",
                "Server settings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _config.Name = draft.Name;
        _config.Kind = draft.Kind;
        _config.UseFtps = draft.UseFtps;
        _config.Host = draft.Host;
        _config.Port = draft.Port;
        _config.User = draft.User;
        _config.PrivateKeyPath = draft.PrivateKeyPath;
        _config.Path = draft.Path;
        _config.TargetOverride = draft.TargetOverride;
        // Keep a key accepted during the test, if it was for these exact connection settings.
        _config.TrustedFingerprint = _testedKey is { } k && k.Kind == draft.Kind && k.Host == draft.Host && k.Port == draft.Port
            ? k.Fingerprint : draft.TrustedFingerprint;
        NewPassword = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        DialogResult = true;
    }
}
