namespace Sdmm.Core.Packaging;

/// <summary>Build targets as named by Alpakit, SMR and ficsit-cli.</summary>
public enum ModTarget
{
    Windows,
    WindowsServer,
    LinuxServer,
}

public static class ModTargets
{
    public static bool TryParse(string? s, out ModTarget target)
    {
        switch (s)
        {
            case "Windows": target = ModTarget.Windows; return true;
            case "WindowsServer": target = ModTarget.WindowsServer; return true;
            case "LinuxServer": target = ModTarget.LinuxServer; return true;
            default: target = default; return false;
        }
    }
}
