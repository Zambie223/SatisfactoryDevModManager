namespace Sdmm.Core.Installs;

/// <summary>
/// File access to one game install, with paths relative to the install root and '/' separators.
/// A local folder now; SFTP/FTP implementations later use the same interface for the rented server.
/// </summary>
public interface IInstallFileSystem
{
    /// <summary>Human-readable location for logs, for example "C:\Games\Satisfactory" or "sftp://host/path".</summary>
    string DisplayRoot { get; }

    bool FileExists(string path);
    bool DirectoryExists(string path);
    /// <summary>Names (not paths) of immediate child directories. Empty if the directory does not exist.</summary>
    IReadOnlyList<string> ListDirectories(string path);
    /// <summary>Names (not paths) of immediate child files. Empty if the directory does not exist.</summary>
    IReadOnlyList<string> ListFiles(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    /// <summary>Writes a file, creating parent directories as needed.</summary>
    void WriteFile(string path, Stream contents);
    void CreateDirectory(string path);
    /// <summary>Deletes a directory and everything in it. No-op if it does not exist.</summary>
    void DeleteDirectory(string path);
    /// <summary>Renames a directory. The destination must not exist; its parent is created if needed.</summary>
    void MoveDirectory(string from, string to);
    /// <summary>Deletes a file. No-op if it does not exist. (Used by mod takeover, to remove SMM's marker.)</summary>
    void DeleteFile(string path) => throw new NotSupportedException($"{GetType().Name} can't delete files.");
}

public static class InstallPaths
{
    public const string ModsDir = "FactoryGame/Mods";
    public const string GameFeaturesDir = "FactoryGame/Mods/GameFeatures";

    public static string Combine(params string[] parts) =>
        string.Join('/', parts.Where(p => p.Length > 0).Select(p => p.Trim('/')));

    /// <summary>Where a mod is installed: Mods/&lt;Ref&gt;, or Mods/GameFeatures/&lt;Ref&gt; for GameFeature mods.</summary>
    public static string ModFolder(string modReference, bool gameFeature) =>
        Combine(gameFeature ? GameFeaturesDir : ModsDir, modReference);
}
