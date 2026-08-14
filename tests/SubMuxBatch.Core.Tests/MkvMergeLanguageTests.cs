using System.Globalization;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class MkvMergeLanguageTests
{
    [Theory]
    [InlineData("ko-KR", "ko")]
    [InlineData("en-US", "en")]
    public async Task InspectionPassesTheCurrentApplicationLanguage(
        string cultureName,
        string expectedLanguage)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            var runner = new LanguageArgumentRunner();

            await new MkvMergeClient("fake-mkvmerge.exe", runner)
                .InspectAsync("missing-source.mkv");

            AssertLanguageArgument(runner.Arguments, expectedLanguage);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("ko-KR", "ko")]
    [InlineData("en-US", "en")]
    public async Task MuxPassesTheCurrentApplicationLanguage(
        string cultureName,
        string expectedLanguage)
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-language-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "subtitle.ass");
            var srt = Path.Combine(root, "subtitle.srt");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[Script Info]\n[V4+ Styles]\n[Events]");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");
            var runner = new LanguageArgumentRunner(output);

            await new MkvMergeClient("fake-mkvmerge.exe", runner)
                .MuxAsync(source, ass, srt, output);

            AssertLanguageArgument(runner.Arguments, expectedLanguage);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertLanguageArgument(
        IReadOnlyList<string>? arguments,
        string expectedLanguage)
    {
        Assert.NotNull(arguments);
        var optionIndex = arguments.ToList().IndexOf("--ui-language");
        Assert.True(optionIndex >= 0);
        Assert.Equal(expectedLanguage, arguments[optionIndex + 1]);
    }

    private sealed class LanguageArgumentRunner(string? outputPath = null) : IProcessRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public async Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Arguments = request.Arguments;
            if (outputPath is not null)
            {
                await File.WriteAllBytesAsync(outputPath, [1], cancellationToken);
            }

            return new ProcessResult(
                0,
                "{\"tracks\":[],\"attachments\":[],\"chapters\":[]}",
                string.Empty);
        }
    }
}
