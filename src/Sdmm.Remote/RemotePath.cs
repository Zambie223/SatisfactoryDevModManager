namespace Sdmm.Remote;

/// <summary>Joins a remote root with an install-relative path, refusing ".." so nothing escapes the root.</summary>
internal static class RemotePath
{
    public static string NormalizeRoot(string root)
    {
        var r = root.Replace('\\', '/').Trim();
        if (r.Length == 0) r = "/";
        return r.Length > 1 ? r.TrimEnd('/') : r;
    }

    public static string Join(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".."))
            throw new InvalidOperationException($"Path '{relative}' is not allowed (contains '.' or '..').");
        if (parts.Length == 0) return root;
        return (root == "/" ? "" : root) + "/" + string.Join('/', parts);
    }

    public static string Parent(string path)
    {
        int i = path.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }
}
