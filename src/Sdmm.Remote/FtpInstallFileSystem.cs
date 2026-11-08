using System.Text;
using Sdmm.Core.Installs;
using FluentFTP;

namespace Sdmm.Remote;

/// <summary>
/// A game install on a server reached over FTP or FTPS. Uses explicit TLS by default, because plain FTP sends the
/// password and files unencrypted. Folder renames (RNFR/RNTO) are used for the stage-then-swap.
/// </summary>
public sealed class FtpInstallFileSystem : IInstallFileSystem, IDisposable
{
    private readonly FtpClient _client;
    private readonly string _root;
    private readonly HostKeyTrust _trust;

    public string DisplayRoot { get; }

    public FtpInstallFileSystem(string host, int port, string user, string password, bool useFtps, string root, HostKeyTrust trust)
    {
        _client = new FtpClient(host, user, password, port);
        _client.Config.ConnectTimeout = 30000;
        _client.Config.EncryptionMode = useFtps ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
        _client.Config.DataConnectionEncryption = useFtps;
        _trust = trust;
        _client.ValidateCertificate += (_, e) =>
        {
            var thumb = e.Certificate is null ? "" : "SHA256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(e.Certificate.GetRawCertData()));
            // Many game hosts use self-signed certificates, so trust is pinned to the certificate rather than a CA chain.
            e.Accept = thumb.Length > 0 && _trust.Check(thumb);
        };
        _root = RemotePath.NormalizeRoot(root);
        DisplayRoot = $"{(useFtps ? "ftps" : "ftp")}://{user}@{host}:{port}{_root}";
    }

    private FtpClient C
    {
        get
        {
            if (!_client.IsConnected)
            {
                try
                {
                    _client.Connect();
                }
                catch (Exception ex) when (_trust.Rejected is not null)
                {
                    throw new InvalidOperationException(_trust.Rejected, ex);
                }
            }
            return _client;
        }
    }

    private string P(string rel) => RemotePath.Join(_root, rel);

    public void TestConnection()
    {
        if (!C.DirectoryExists(_root)) throw new DirectoryNotFoundException($"{DisplayRoot} does not exist on the server.");
    }

    public bool FileExists(string path) => C.FileExists(P(path));
    public bool DirectoryExists(string path) => C.DirectoryExists(P(path));

    private IEnumerable<FtpListItem> List(string full) =>
        C.DirectoryExists(full) ? C.GetListing(full).Where(e => e.Name is not "." and not "..") : [];

    public IReadOnlyList<string> ListDirectories(string path) =>
        List(P(path)).Where(e => e.Type == FtpObjectType.Directory).Select(e => e.Name).ToList();

    public IReadOnlyList<string> ListFiles(string path) =>
        List(P(path)).Where(e => e.Type == FtpObjectType.File).Select(e => e.Name).ToList();

    public string ReadAllText(string path)
    {
        if (!C.DownloadBytes(out var bytes, P(path)))
            throw new FileNotFoundException($"Could not download {P(path)}.");
        return Encoding.UTF8.GetString(bytes);
    }

    public void WriteAllText(string path, string contents)
    {
        var status = C.UploadBytes(new UTF8Encoding(false).GetBytes(contents), P(path), FtpRemoteExists.Overwrite, createRemoteDir: true);
        if (status == FtpStatus.Failed) throw new RemoteRefusedException($"The server refused the upload of {P(path)}.");
    }

    public void WriteFile(string path, Stream contents)
    {
        var status = C.UploadStream(contents, P(path), FtpRemoteExists.Skip, createRemoteDir: true);
        if (status == FtpStatus.Failed) throw new RemoteRefusedException($"The server refused the upload of {P(path)}.");
        if (status != FtpStatus.Success) throw new IOException($"Upload of {P(path)} failed ({status}); the file may already exist.");
    }

    public void CreateDirectory(string path) => C.CreateDirectory(P(path), force: true);

    public void DeleteDirectory(string path)
    {
        var full = P(path);
        if (C.DirectoryExists(full)) C.DeleteDirectory(full);
    }

    public void MoveDirectory(string from, string to)
    {
        var src = P(from);
        var dst = P(to);
        if (C.DirectoryExists(dst) || C.FileExists(dst)) throw new IOException($"Cannot move '{from}' to '{to}': the destination already exists.");
        C.CreateDirectory(RemotePath.Parent(dst), force: true);
        C.Rename(src, dst);
    }

    public void DeleteFile(string path)
    {
        var full = P(path);
        if (C.FileExists(full)) C.DeleteFile(full);
    }

    public void Dispose()
    {
        if (_client.IsConnected) _client.Disconnect();
        _client.Dispose();
    }
}
