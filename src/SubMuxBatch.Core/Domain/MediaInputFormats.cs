namespace SubMuxBatch.Core.Domain;

public static class MediaInputFormats
{
    public static IReadOnlyList<string> VideoExtensions { get; } = Array.AsReadOnly(
        [".mkv", ".mp4", ".m4v", ".mov", ".avi", ".ts", ".mts", ".m2ts", ".webm"]);

    public static IReadOnlyList<string> SubtitleExtensions { get; } = Array.AsReadOnly(
        [".ass", ".srt", ".smi"]);

    public static IReadOnlyList<string> SupportedExtensions { get; } = Array.AsReadOnly(
        VideoExtensions.Concat(SubtitleExtensions).ToArray());

    public static string VideoDialogPattern { get; } = CreateDialogPattern(VideoExtensions);
    public static string SubtitleDialogPattern { get; } = CreateDialogPattern(SubtitleExtensions);
    public static string SupportedDialogPattern { get; } = CreateDialogPattern(SupportedExtensions);

    private static readonly HashSet<string> VideoExtensionSet = new(
        VideoExtensions,
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SubtitleExtensionSet = new(
        SubtitleExtensions,
        StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string path) => VideoExtensionSet.Contains(Path.GetExtension(path));

    public static bool IsSubtitle(string path) => SubtitleExtensionSet.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path) => IsVideo(path) || IsSubtitle(path);

    private static string CreateDialogPattern(IEnumerable<string> extensions) =>
        string.Join(';', extensions.Select(static extension => $"*{extension}"));
}
