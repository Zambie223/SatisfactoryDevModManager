using System.Text;
using Sdmm.Core.Installs;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Sdmm.Remote;

/// <summary>
/// A game install on a server reached over SFTP. Directory renames are atomic on the server, so the installer's
/// stage-then-swap works the same as locally.
/// </summary>
public sealed class SftpInstallFileSystem : IInstallFileSystem, IDisposable
{
    private readonly SftpClient _client;
    private readonly string _root;
    private readonly HostKeyTrust _trust;

    public string DisplayRoot { get; }

    /// <param name="trust">Decides whether to accept the server's SSH host key (see <see cref="HostKeyTrust"/>).</param>
    public SftpInstallFileSystem(string host, int port, string user, string? password, string? privateKeyPath, string root, HostKeyTrust trust)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(privateKeyPath))
            methods.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(privateKeyPath)));
        if (!string.IsNullOrEmpty(password))
            methods.Add(new PasswordAuthenticationMethod(user, password));
        if (methods.Count == 0)
            throw new ArgumentException("SFTP needs a password or a private key file.");

        _client = new SftpClient(new ConnectionInfo(host, port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(30) });
        _trust = trust;
        _client.HostKeyReceived += (_, e) => e.CanTrust = _trust.Check("SHA256:" + e.FingerPrintSHA256);
        _root = RemotePath.NormalizeRoot(root);
        DisplayRoot = $"sftp://{user}@{host}:{port}{_root}";
    }

    private SftpClient C
    {
        get
        {
            if (!_client.IsConnected)
            {
                try
                {
                    _client.Connect();
                }
                catch (SshConnectionException ex) when (_trust.Rejected is not null)
                {
                    throw new InvalidOperationException(_trust.Rejected, ex);
                }
            }
            return _client;
        }
    }

    private string P(string rel) => RemotePath.Join(_root, rel);

    /// <summary>Connects and checks the root exists. Useful for a "Test connection" button.</summary>
    public void TestConnection()
    {
        if (!C.Exists(_root)) throw new DirectoryNotFoundException($"{DisplayRoot} does not exist on the server.");
    }

    private bool TryAttrs(string path, out bool isDirectory)
    {
        try
        {
            isDirectory = C.GetAttributes(path).IsDirectory;
            return true;
        }
        catch (SftpPathNotFoundException)
        {
            isDirectory = false;
            return false;
        }
    }

    public bool FileExists(string path) => TryAttrs(P(path), out var dir) && !dir;
    public bool DirectoryExists(string path) => TryAttrs(P(path), out var dir) && dir;

    private IEnumerable<Renci.SshNet.Sftp.ISftpFile> List(string full) =>
        TryAttrs(full, out var dir) && dir ? C.ListDirectory(full).Where(e => e.Name is not "." and not "..") : [];

    public IReadOnlyList<string> ListDirectories(string path) => List(P(path)).Where(e => e.IsDirectory).Select(e => e.Name).ToList();
    public IReadOnlyList<string> ListFiles(string path) => List(P(path)).Where(e => e.IsRegularFile).Select(e => e.Name).ToList();

    public string ReadAllText(string path)
    {
        using var ms = new MemoryStream();
        C.DownloadFile(P(path), ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void WriteAllText(string path, string contents)
    {
        var full = P(path);
        MkDirs(RemotePath.Parent(full));
        using var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(contents));
        C.UploadFile(ms, full, canOverride: true);
    }

    public void WriteFile(string path, Stream contents)
    {
        var full = P(path);
        MkDirs(RemotePath.Parent(full));
        C.UploadFile(contents, full, canOverride: false);
    }

    public void CreateDirectory(string path) => MkDirs(P(path));

    private void MkDirs(string full)
    {
        if (TryAttrs(full, out var dir))
        {
            if (!dir) throw new IOException($"{full} exists on the server and is not a folder.");
            return;
        }
        var parent = RemotePath.Parent(full);
        if (parent != full) MkDirs(parent);
        C.CreateDirectory(full);
    }

    public void DeleteDirectory(string path)
    {
        var full = P(path);
        if (!TryAttrs(full, out var dir)) return;
        if (!dir) throw new IOException($"{full} is not a folder.");
        DeleteRecursive(full);
    }

    private void DeleteRecursive(string full)
    {
        foreach (var e in C.ListDirectory(full).Where(e => e.Name is not "." and not "..").ToList())
        {
            if (e.IsDirectory) DeleteRecursive(e.FullName);
            else C.DeleteFile(e.FullName);
        }
        C.DeleteDirectory(full);
    }

    public void MoveDirectory(string from, string to)
    {
        var src = P(from);
        var dst = P(to);
        if (TryAttrs(dst, out _)) throw new IOException($"Cannot move '{from}' to '{to}': the destination already exists.");
        MkDirs(RemotePath.Parent(dst));
        C.RenameFile(src, dst);
    }

    public void DeleteFile(string path)
    {
        var full = P(path);
        if (TryAttrs(full, out var dir) && !dir) C.DeleteFile(full);
    }

    public void Dispose()
    {
        if (_client.IsConnected) _client.Disconnect();
        _client.Dispose();
    }
}
