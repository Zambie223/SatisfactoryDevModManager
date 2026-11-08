using System.Text.RegularExpressions;

namespace Sdmm.Core.Versioning;

/// <summary>
/// A version range in the syntax SML accepts (".uplugin" Plugins[].SemVersion, GameVersion, RemoteVersionRange).
/// This is a port of SML's FVersionRange (Mods/SML/Source/SML/Private/Util/SemVersion.cpp), so that the tool
/// predicts what SML will accept at game startup. It follows SML's semantics, which differ from node-semver:
/// <list type="bullet">
/// <item>Prerelease versions are NOT excluded from ranges (for example "^1.0.0" matches "1.5.0-dev.1").</item>
/// <item>Tilde on a full version allows only that patch: "~1.2.3" is "&gt;=1.2.3 &lt;1.2.4" (npm: "&lt;1.3.0").</item>
/// </list>
/// </summary>
public sealed partial class VersionRange
{
    internal const long Wildcard = -1;
    internal const long Unspecified = -2;

    [GeneratedRegex(@"^(~|v|=|<=|<|>|>=|\^)?(X|x|\*|0|[1-9]\d*)(?:\.(X|x|\*|0|[1-9]\d*)(?:\.(X|x|\*|0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplatePattern();

    private readonly List<List<Comparator>> _collections;

    /// <summary>The original text this range was parsed from.</summary>
    public string Text { get; }

    private VersionRange(string text, List<List<Comparator>> collections)
    {
        Text = text;
        _collections = collections;
    }

    public static VersionRange Any { get; } = new("*", [[new Comparator(Op.Equals, new Raw(Wildcard, Wildcard, Wildcard, ""))]]);

    /// <summary>Equivalent of FVersionRange::CreateRangeWithMinVersion (SML's default RemoteVersionRange).</summary>
    public static VersionRange AtLeast(SemVersion min) =>
        new(">=" + min, [[new Comparator(Op.GreaterEquals, Raw.From(min))]]);

    public static bool TryParse(string? text, out VersionRange? range, out string? error)
    {
        range = null;
        if (text is null)
        {
            error = "Version range cannot be empty";
            return false;
        }
        // SML: String.ParseIntoArray(CollectionStrings, "||") culls empty pieces.
        var pieces = text.Split("||", StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0)
        {
            error = "Version range cannot be empty";
            return false;
        }
        var collections = new List<List<Comparator>>();
        foreach (var piece in pieces)
        {
            if (!TryParseCollection(piece, out var collection, out error)) return false;
            collections.Add(collection!);
        }
        range = new VersionRange(text, collections);
        error = null;
        return true;
    }

    public static VersionRange Parse(string text) =>
        TryParse(text, out var r, out var e) ? r! : throw new FormatException(e);

    public bool Matches(SemVersion version) =>
        _collections.Any(c => c.All(comp => comp.Matches(version)));

    public override string ToString() => Text;

    // ---- parsing -------------------------------------------------------------------------------------------

    private enum Op { Equals, Greater, GreaterEquals, Less, LessEquals, Caret, Tilde }

    /// <summary>A version that may contain wildcard or unspecified numbers.</summary>
    private readonly record struct Raw(long Major, long Minor, long Patch, string PreRelease)
    {
        public static Raw From(SemVersion v) => new(v.Major, v.Minor, v.Patch, v.PreRelease);

        public bool HasSpecial => IsSpecial(Major) || IsSpecial(Minor) || IsSpecial(Patch);

        public Raw RemoveSpecial(long replacement) => this with
        {
            Major = IsSpecial(Major) ? replacement : Major,
            Minor = IsSpecial(Minor) ? replacement : Minor,
            Patch = IsSpecial(Patch) ? replacement : Patch,
        };

        private static bool IsSpecial(long n) => n == Wildcard || n == Unspecified;
    }

    /// <summary>Compares a concrete version against a raw version with no special numbers.</summary>
    private static int Compare(SemVersion v, Raw r)
    {
        int c = v.Major.CompareTo(r.Major);
        if (c != 0) return c;
        c = v.Minor.CompareTo(r.Minor);
        if (c != 0) return c;
        c = v.Patch.CompareTo(r.Patch);
        if (c != 0) return c;
        return SemVersion.ComparePreRelease(v.PreRelease, r.PreRelease);
    }

    private static string? ParseTemplate(string s, out Raw version, out Op op)
    {
        version = default;
        op = Op.Equals;
        var m = TemplatePattern().Match(s);
        if (!m.Success) return $"Version '{s}' doesn't match SemVer pattern";
        switch (m.Groups[1].Value)
        {
            case "<=": op = Op.LessEquals; break;
            case "<": op = Op.Less; break;
            case ">=": op = Op.GreaterEquals; break;
            case ">": op = Op.Greater; break;
            case "^": op = Op.Caret; break;
            case "~": op = Op.Tilde; break;
            default: op = Op.Equals; break; // "=", "v" or nothing
        }
        if (!TryNumber(m.Groups[2].Value, out var major) ||
            !TryNumber(m.Groups[3].Value, out var minor) ||
            !TryNumber(m.Groups[4].Value, out var patch))
        {
            return $"Version '{s}' has a number that is too large";
        }
        if ((major == Wildcard || minor == Wildcard) && patch != Wildcard && patch != Unspecified)
            return "Wildcard cannot be followed by version number";
        if (major == Wildcard && minor != Wildcard && minor != Unspecified)
            return "Wildcard cannot be followed by version number";
        version = new Raw(major, minor, patch, m.Groups[5].Value);
        return null;
    }

    private static bool TryNumber(string s, out long n)
    {
        if (s is "X" or "x" or "*") { n = Wildcard; return true; }
        if (s.Length == 0) { n = Unspecified; return true; }
        return long.TryParse(s, out n);
    }

    private static bool TryParseComparator(string s, out Comparator? comparator, out string? error)
    {
        comparator = null;
        error = ParseTemplate(s, out var v, out var op);
        if (error is not null) return false;
        if (op is Op.Equals or Op.Caret)
        {
            // Partial versions are X-ranges for "=", and caret treats unspecified numbers as wildcards.
            v = v.RemoveSpecial(Wildcard);
        }
        else
        {
            if (v.Major == Wildcard || v.Minor == Wildcard || v.Patch == Wildcard)
            {
                error = "Unexpected Wildcard inside normal version comparator";
                return false;
            }
            // Tilde keeps unspecified numbers; it uses them to pick its upper bound.
            if (op != Op.Tilde) v = v.RemoveSpecial(0);
        }
        comparator = new Comparator(op, v);
        return true;
    }

    private static bool TryParseHyphenSide(string s, out Raw v, out string? error)
    {
        error = ParseTemplate(s, out v, out var op);
        if (error is not null) return false;
        if (op != Op.Equals) { error = "Unexpected version comparator inside Hyphen Version Range"; return false; }
        if (v.Major == Wildcard || v.Minor == Wildcard || v.Patch == Wildcard)
        {
            error = "Unexpected wildcard inside Hyphen Version Range";
            return false;
        }
        return true;
    }

    private static bool TryParseHyphen(string left, string right, List<Comparator> output, out string? error)
    {
        if (!TryParseHyphenSide(left, out var l, out error) | !TryParseHyphenSide(right, out var r, out var rError))
        {
            error ??= rError;
            return false;
        }
        var lower = l.RemoveSpecial(0);
        Raw upper;
        bool inclusive;
        if (r.Major == Unspecified)
        {
            error = "Invalid Major version in right side of Hyphen Version Range";
            return false;
        }
        if (r.Minor == Unspecified)
        {
            upper = new Raw(r.Major + 1, 0, 0, "0");
            inclusive = false;
        }
        else if (r.Patch == Unspecified)
        {
            upper = new Raw(r.Major, r.Minor + 1, 0, "0");
            inclusive = false;
        }
        else
        {
            upper = r;
            inclusive = true;
        }
        output.Add(new Comparator(Op.GreaterEquals, lower));
        output.Add(new Comparator(inclusive ? Op.LessEquals : Op.Less, upper));
        return true;
    }

    /// <summary>
    /// Space-separated comparators ANDed together, with "a - b" hyphen ranges. Mirrors
    /// FVersionComparatorCollection::ParseVersionCollection: a '-' counts as a hyphen only at the start of a
    /// token (after a space); inside a token it is part of a prerelease.
    /// </summary>
    private static bool TryParseCollection(string text, out List<Comparator>? result, out string? error)
    {
        result = null;
        var list = new List<Comparator>();
        var tokens = new List<string>();
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw;
            while (t.StartsWith('-'))
            {
                tokens.Add("-");
                t = t[1..];
            }
            if (t.Length > 0) tokens.Add(t);
        }

        string? pendingLeft = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (tok == "-")
            {
                error = "Unexpected hyphen";
                return false;
            }
            bool hyphenFollows = i + 1 < tokens.Count && tokens[i + 1] == "-";
            if (hyphenFollows)
            {
                if (pendingLeft is not null) { error = "Unexpected hyphen"; return false; }
                if (i + 2 >= tokens.Count || tokens[i + 2] == "-") { error = "Unexpected hyphen"; return false; }
                pendingLeft = tok;
                i++; // skip the hyphen
                continue;
            }
            if (pendingLeft is not null)
            {
                if (!TryParseHyphen(pendingLeft, tok, list, out error)) return false;
                pendingLeft = null;
                continue;
            }
            if (!TryParseComparator(tok, out var comparator, out error)) return false;
            list.Add(comparator!);
        }

        if (list.Count == 0)
        {
            error = "Version Comparator Collection cannot be empty";
            return false;
        }
        result = list;
        error = null;
        return true;
    }

    // ---- matching ------------------------------------------------------------------------------------------

    private sealed class Comparator(Op op, Raw version)
    {
        public bool Matches(SemVersion v)
        {
            var clean = version.RemoveSpecial(0);
            int result = Compare(v, clean);
            switch (op)
            {
                case Op.GreaterEquals: return result >= 0;
                case Op.Greater: return result > 0;
                case Op.LessEquals: return result <= 0;
                case Op.Less: return result < 0;
                case Op.Caret:
                    if (result < 0) return false;
                    return !CaretMax(version, out var cMax) || Compare(v, cMax) < 0;
                case Op.Tilde:
                    if (result < 0) return false;
                    return !TildeMax(version, out var tMax) || Compare(v, tMax) < 0;
                default: // Equals, possibly an X-range
                    if (!version.HasSpecial) return result == 0;
                    if (result < 0) return false;
                    return !XRangeMax(version, out var xMax) || Compare(v, xMax) < 0;
            }
        }
    }

    private static bool CaretMax(Raw lower, out Raw max)
    {
        max = new Raw(0, 0, 0, "");
        if (lower.HasSpecial)
        {
            if (lower.Major == Wildcard) return false;
            // SML leaves the result undefined for "^0" (major 0 with a wildcard minor); this treats it as <1.0.0.
            if (lower.Minor == Wildcard) { max = new Raw(lower.Major + 1, 0, 0, ""); return true; }
            if (lower.Patch == Wildcard)
            {
                max = lower.Major == 0 ? new Raw(0, lower.Minor + 1, 0, "") : new Raw(lower.Major + 1, 0, 0, "");
            }
            return true;
        }
        if (lower.Major == 0)
            max = lower.Minor == 0 ? new Raw(0, 0, lower.Patch + 1, "") : new Raw(0, lower.Minor + 1, 0, "");
        else
            max = new Raw(lower.Major + 1, 0, 0, "");
        return true;
    }

    private static bool TildeMax(Raw lower, out Raw max)
    {
        max = new Raw(0, 0, 0, "");
        if (lower.Major == Unspecified) return false;
        if (lower.Minor == Unspecified) max = new Raw(lower.Major + 1, 0, 0, "");
        else if (lower.Patch == Unspecified) max = new Raw(lower.Major, lower.Minor + 1, 0, "");
        else max = new Raw(lower.Major, lower.Minor, lower.Patch + 1, "");
        return true;
    }

    private static bool XRangeMax(Raw lower, out Raw max)
    {
        max = lower;
        if (lower.Major == Wildcard) return false;
        if (lower.Minor is Wildcard or Unspecified) { max = new Raw(lower.Major + 1, 0, 0, ""); return true; }
        if (lower.Patch is Wildcard or Unspecified) { max = new Raw(lower.Major, lower.Minor + 1, 0, ""); return true; }
        return true;
    }
}
