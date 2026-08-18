using System.Globalization;
using System.Runtime.InteropServices;

namespace SubMuxBatch.Core.Updates;

public readonly record struct UpdateVersion(int Year, int Month, int Day) : IComparable<UpdateVersion>
{
    public static bool TryParse(string? value, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        var metadataIndex = text.IndexOfAny(['+', '-']);
        if (metadataIndex >= 0)
        {
            text = text[..metadataIndex];
        }

        if (!DateOnly.TryParseExact(
                text,
                "yyyy.MM.dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return false;
        }

        version = new UpdateVersion(date.Year, date.Month, date.Day);
        return true;
    }

    public int CompareTo(UpdateVersion other) =>
        new DateOnly(Year, Month, Day).CompareTo(new DateOnly(other.Year, other.Month, other.Day));

    public override string ToString() => $"{Year:0000}.{Month:00}.{Day:00}";

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;
}

public sealed record UpdateRelease(
    UpdateVersion Version,
    string TagName,
    string ReleasePageUrl,
    string AssetName,
    Uri DownloadUrl,
    long AssetSize,
    string? Sha256Digest);

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public int Percentage => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(BytesReceived * 100L / TotalBytes, 0, 100);
}

public sealed record PreparedUpdate(
    UpdateRelease Release,
    string UpdateRoot,
    string PackageDirectory,
    string PackageExecutablePath);

public static class UpdateRuntime
{
    public static string Current => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64"
    };
}
