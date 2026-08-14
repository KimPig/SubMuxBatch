namespace SubMuxBatch.Core.Domain;

public static class OutputFileNaming
{
    public const string DefaultPrefix = "SubMux_";

    public static string Create(string videoPath, string prefix) =>
        prefix + Path.GetFileNameWithoutExtension(videoPath) + ".mkv";
}
