namespace Sdmm.Core.Installs;

/// <summary>An install on a local or mapped drive.</summary>
public sealed class LocalInstallFileSystem : IInstallFileSystem
{
    public string Root { get; }
    public string DisplayRoot => Root;

    public LocalInstallFileSystem(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>Maps a relative path to a full path and refuses anything that escapes the root.</summary>
    public string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
        // A drive root keeps its separator after trimming ("D:\"), so don't add a second one.
        var rootWithSep = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path '{path}' resolves outside the install root '{Root}'.");
        }
        return full;
    }

    public bool FileExists(string path) => File.Exists(Resolve(path));
    public bool DirectoryExists(string path) => Directory.Exists(Resolve(path));

    public IReadOnlyList<string> ListDirectories(string path)
    {
        var full = Resolve(path);
        return Directory.Exists(full)
            ? Directory.GetDirectories(full).Select(d => Path.GetFileName(d)!).ToList()
            : [];
    }

    public IReadOnlyList<string> ListFiles(string path)
    {
        var full = Resolve(path);
        return Directory.Exists(full)
            ? Directory.GetFiles(full).Select(f => Path.GetFileName(f)!).ToList()
            : [];
    }

    public string ReadAllText(string path) => File.ReadAllText(Resolve(path));

    public void WriteAllText(string path, string contents)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    public void WriteFile(string path, Stream contents)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        contents.CopyTo(fs);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(Resolve(path));

    public void DeleteDirectory(string path)
    {
        var full = Resolve(path);
        if (!Directory.Exists(full)) return;
        // Clear read-only flags first; Directory.Delete fails on read-only files.
        foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var attr = File.GetAttributes(f);
            if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(full, recursive: true);
    }

    public void MoveDirectory(string from, string to)
    {
        var src = Resolve(from);
        var dst = Resolve(to);
        if (Directory.Exists(dst) || File.Exists(dst))
            throw new IOException($"Cannot move '{from}' to '{to}': the destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        Directory.Move(src, dst);
    }

    public void DeleteFile(string path)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) return;
        var attr = File.GetAttributes(full);
        if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(full, attr & ~FileAttributes.ReadOnly);
        File.Delete(full);
    }
}
