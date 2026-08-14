namespace SubMuxBatch.Core.Domain;

internal static class WorkspaceNaming
{
    public const string CurrentPrefix = ".submuxbatch-";
    public const string LegacyPrefix = ".subtitlebatch-";

    public static bool IsWorkspaceDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(CurrentPrefix, StringComparison.OrdinalIgnoreCase)
               || name.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase);
    }
}