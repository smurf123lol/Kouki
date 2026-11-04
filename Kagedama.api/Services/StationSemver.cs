using System.Globalization;

namespace Kouki.Kagedama.Api.Services;

/// <summary>
/// Разбор и сравнение версий прошивки станции по Semantic Versioning (X.Y.Z[-prerelease]).
/// </summary>
public readonly record struct StationSemver(int Major, int Minor, int Patch, string Prerelease = "") : IComparable<StationSemver>
{
    /// <summary>
    /// Пробует разобрать строку версии.
    /// </summary>
    public static bool TryParse(string? input, out StationSemver version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string s = input.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        string prerelease = string.Empty;
        int dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            prerelease = s[(dashIdx + 1)..].Trim();
            s = s[..dashIdx].Trim();
        }

        string[] parts = s.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        if (major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        version = new StationSemver(major, minor, patch, prerelease);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(StationSemver other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;

        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;

        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        if (string.IsNullOrEmpty(Prerelease) && !string.IsNullOrEmpty(other.Prerelease)) return 1;
        if (!string.IsNullOrEmpty(Prerelease) && string.IsNullOrEmpty(other.Prerelease)) return -1;

        return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return string.IsNullOrEmpty(Prerelease)
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";
    }

    public static bool operator <(StationSemver left, StationSemver right) => left.CompareTo(right) < 0;
    public static bool operator <=(StationSemver left, StationSemver right) => left.CompareTo(right) <= 0;
    public static bool operator >(StationSemver left, StationSemver right) => left.CompareTo(right) > 0;
    public static bool operator >=(StationSemver left, StationSemver right) => left.CompareTo(right) >= 0;
}
