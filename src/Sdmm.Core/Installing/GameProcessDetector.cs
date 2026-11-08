using System.ComponentModel;
using System.Diagnostics;

namespace Sdmm.Core.Installing;

public interface IGameProcessDetector
{
    /// <summary>Descriptions of running game or server processes that may be using this install. Empty if none.</summary>
    IReadOnlyList<string> FindRunning(string localRoot);
}

/// <summary>
/// Finds running Satisfactory processes (FactoryGame*, FactoryServer*, UnrealServer*). Pak files are locked while
/// the game runs, and replacing a mod under a running game breaks it, so installs are refused meanwhile.
/// </summary>
public sealed class GameProcessDetector : IGameProcessDetector
{
    private static readonly string[] Prefixes = ["FactoryGame", "FactoryServer", "UnrealServer"];

    public IReadOnlyList<string> FindRunning(string localRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar; // a drive root ("D:\") already has one
        var found = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!Prefixes.Any(prefix => p.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
                string? exe = null;
                try
                {
                    exe = p.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Access denied or the process exited. Conservatively count it as possibly this install.
                }
                if (exe is null)
                    found.Add($"{p.ProcessName} (pid {p.Id}; could not tell which install it runs from)");
                else if (exe.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    found.Add($"{p.ProcessName} (pid {p.Id})");
            }
        }
        return found;
    }
}
