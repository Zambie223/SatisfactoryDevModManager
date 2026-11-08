using Sdmm.Core.Installs;

namespace Sdmm.Core.Diagnostics;

/// <summary>
/// Writes every change to a game or server folder to the detailed log (<see cref="FileLog"/>): files written, folders
/// created, moved and deleted. Reads aren't logged; they change nothing and would crowd out the changes. Costs a single
/// flag check per call while the log is off.
/// </summary>
public sealed class LoggingInstallFileSystem(IInstallFileSystem inner) : IInstallFileSystem
{
    public IInstallFileSystem Inner => inner;
    public string DisplayRoot => inner.DisplayRoot;

    public bool FileExists(string path) => inner.FileExists(path);
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);
    public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
    public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
    public string ReadAllText(string path) => inner.ReadAllText(path);

    public void WriteAllText(string path, string contents) => Do("write", path, () => inner.WriteAllText(path, contents), $" ({contents.Length} chars)");
    public void WriteFile(string path, Stream contents) =>
        Do("write", path, () => inner.WriteFile(path, contents), contents.CanSeek ? $" ({contents.Length} bytes)" : "");
    public void CreateDirectory(string path) => Do("create folder", path, () => inner.CreateDirectory(path));
    public void DeleteDirectory(string path) => Do("delete folder", path, () => inner.DeleteDirectory(path));
    public void DeleteFile(string path) => Do("delete", path, () => inner.DeleteFile(path));
    public void MoveDirectory(string from, string to)
    {
        if (!FileLog.Enabled) { inner.MoveDirectory(from, to); return; }
        try
        {
            inner.MoveDirectory(from, to);
            FileLog.Write("fs", $"move folder {Where(from)} -> {to}");
        }
        catch (Exception ex)
        {
            FileLog.Write("fs", $"move folder {Where(from)} -> {to} FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private string Where(string path) => $"{inner.DisplayRoot.TrimEnd('/', '\\')}/{path}";

    private void Do(string what, string path, Action action, string detail = "")
    {
        if (!FileLog.Enabled) { action(); return; }
        try
        {
            action();
            FileLog.Write("fs", $"{what} {Where(path)}{detail}");
        }
        catch (Exception ex)
        {
            FileLog.Write("fs", $"{what} {Where(path)}{detail} FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
