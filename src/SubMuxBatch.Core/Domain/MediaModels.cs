namespace SubMuxBatch.Core.Domain;

public readonly record struct MediaKey(string DirectoryPath, string Stem)
{
    public string Canonical => Path.Combine(DirectoryPath, Stem);

    public static MediaKey FromPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("파일의 상위 폴더를 확인할 수 없습니다.", nameof(path));
        return new MediaKey(directory, Path.GetFileNameWithoutExtension(fullPath));
    }
}

public sealed record MediaSet(
    MediaKey Key,
    string? VideoPath,
    string? AssPath,
    string? SrtPath,
    string? SmiPath,
    IReadOnlyList<string>? VideoCandidates = null)
{
    public bool HasAnySubtitle => AssPath is not null || SrtPath is not null || SmiPath is not null;
    public IReadOnlyList<string> CandidateVideoPaths => VideoCandidates
        ?? (VideoPath is null ? [] : [VideoPath]);
    public bool HasVideoConflict => CandidateVideoPaths.Count > 1;

    public MediaSet Merge(MediaSet other)
    {
        if (!string.Equals(Key.Canonical, other.Key.Canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("서로 다른 미디어 묶음은 합칠 수 없습니다.");
        }

        var mergedVideoPaths = (other.VideoCandidates is not null
                ? other.VideoCandidates
                : other.VideoPath is null ? CandidateVideoPaths : [other.VideoPath])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return this with
        {
            VideoPath = mergedVideoPaths.Length == 1 ? mergedVideoPaths[0] : null,
            VideoCandidates = mergedVideoPaths,
            AssPath = other.AssPath ?? AssPath,
            SrtPath = other.SrtPath ?? SrtPath,
            SmiPath = other.SmiPath ?? SmiPath
        };
    }
}

public enum AssSourceKind
{
    Existing,
    ConvertFromSrt
}

public enum SrtSourceKind
{
    Existing,
    ConvertFromAss,
    ConvertFromSmi
}

public sealed record ConversionPlan(
    bool IsValid,
    AssSourceKind AssSource,
    SrtSourceKind SrtSource,
    string Description,
    IReadOnlyList<string> Warnings,
    string? Error = null);

public enum JobState
{
    Ready,
    Invalid,
    Queued,
    ConvertingSmiToSrt,
    ConvertingAssToSrt,
    ConvertingSrtToAss,
    Muxing,
    Verifying,
    Succeeded,
    SucceededWithWarnings,
    Skipped,
    Failed,
    Cancelling,
    Cancelled
}

public sealed record JobProgress(JobState State, int Percent, string Message);

public sealed record JobResult(
    JobState State,
    string? OutputPath,
    IReadOnlyList<string> Warnings,
    string? Error = null);

public sealed class JobSkippedException(string message) : InvalidOperationException(message);
