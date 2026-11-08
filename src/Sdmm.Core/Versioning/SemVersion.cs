using System.Text.RegularExpressions;

namespace Sdmm.Core.Versioning;

/// <summary>
/// A semantic version (major.minor.patch[-prerelease][+build]).
/// Ordering follows SemVer 2.0 precedence. Build metadata is ignored for ordering and equality.
/// </summary>
/// <remarks>
/// SML's FVersion::Compare (SemVersion.cpp, ComparePreReleaseField) returns -1 instead of 0 when two numeric
/// prerelease fields are equal, so for example SML orders "1.0.0-rc.1.6" below "1.0.0-rc.1.5". This type uses the
/// correct SemVer ordering instead. The two only disagree when an equal numeric field comes before a differing field.
/// </remarks>
public sealed partial class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    // Same grammar as SML's VersionRegex, restricted to a complete version (no comparator or wildcards).
    // SML accepts a leading "v" or "=" on a plain version, so this does too.
    [GeneratedRegex(@"^(?:v|=)?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public long Major { get; }
    public long Minor { get; }
    public long Patch { get; }
    /// <summary>Prerelease identifiers without the leading '-', or empty.</summary>
    public string PreRelease { get; }
    /// <summary>Build metadata without the leading '+', or empty.</summary>
    public string Build { get; }

    public bool IsPreRelease => PreRelease.Length > 0;

    public SemVersion(long major, long minor, long patch, string preRelease = "", string build = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease ?? "";
        Build = build ?? "";
    }

    public static bool TryParse(string? text, out SemVersion? version, out string? error)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Version is empty";
            return false;
        }
        var m = Pattern().Match(text.Trim());
        if (!m.Success)
        {
            error = $"'{text}' is not a valid semantic version (expected major.minor.patch[-prerelease][+build])";
            return false;
        }
        if (!long.TryParse(m.Groups[1].Value, out var major) ||
            !long.TryParse(m.Groups[2].Value, out var minor) ||
            !long.TryParse(m.Groups[3].Value, out var patch))
        {
            error = $"'{text}' has a version number that is too large";
            return false;
        }
        version = new SemVersion(major, minor, patch, m.Groups[4].Value, m.Groups[5].Value);
        error = null;
        return true;
    }

    public static bool TryParse(string? text, out SemVersion? version) => TryParse(text, out version, out _);

    public static SemVersion Parse(string text) =>
        TryParse(text, out var v, out var error) ? v! : throw new FormatException(error);

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    internal static int ComparePreRelease(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return 1;   // a release is greater than any prerelease
        if (b.Length == 0) return -1;
        var af = a.Split('.');
        var bf = b.Split('.');
        int n = Math.Min(af.Length, bf.Length);
        for (int i = 0; i < n; i++)
        {
            int c = CompareIdentifier(af[i], bf[i]);
            if (c != 0) return c;
        }
        return af.Length.CompareTo(bf.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        bool an = IsNumeric(a), bn = IsNumeric(b);
        if (an && bn)
        {
            // Numeric identifiers have no leading zeros (enforced by the grammar), so compare by length then digits.
            int c = a.Length.CompareTo(b.Length);
            return c != 0 ? c : string.CompareOrdinal(a, b);
        }
        if (an) return -1; // numeric identifiers have lower precedence than alphanumeric
        if (bn) return 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    private static bool IsNumeric(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public static bool operator ==(SemVersion? a, SemVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemVersion? a, SemVersion? b) => !(a == b);
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;

    public override string ToString()
    {
        var s = $"{Major}.{Minor}.{Patch}";
        if (PreRelease.Length > 0) s += "-" + PreRelease;
        if (Build.Length > 0) s += "+" + Build;
        return s;
    }
}
