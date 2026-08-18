using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class MkvMergeWarningTests
{
    [Fact]
    public async Task CollectsDetailedWarningsFromBothStreamsWithoutDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-warning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[Script Info]\n[V4+ Styles]\n[Events]");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");

            var runner = new WarningRunner(
                output,
                1,
                "#GUI#progress 50%\n#GUI#warning 첫 번째 경고\n#GUI#warning 중복 경고\n",
                "#GUI#warning 중복 경고\n  #GUI#warning 두 번째 경고\n");
            var result = await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output);

            Assert.True(result.HadWarnings);
            Assert.Equal(new[] { "첫 번째 경고", "중복 경고", "두 번째 경고" }, result.Warnings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UsesFallbackWhenExitCodeSignalsWarningWithoutDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-warning-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[Script Info]\n[V4+ Styles]\n[Events]");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");

            var runner = new WarningRunner(output, 1, "#GUI#progress 100%\n", string.Empty);
            var result = await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output);

            var warning = Assert.Single(result.Warnings);
            Assert.Contains("상세 내용을 제공하지 않았습니다", warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("#GUI#warning 'secondary.srt' track 0: Warning in line 348: The start timestamp is smaller than that of the previous entry. All entries from this file will be sorted by their start time.\n")]
    [InlineData("#GUI#warning 'secondary.srt' 트랙 0: 348번째 경고: 시작 시간 타임코드가 이전 항목의 타임코드보다 작습니다. 파일의 모든 항목은 시작 시간으로 정렬됩니다.\n")]
    public async Task IgnoresSubtitleOrderingNotice(string warningOutput)
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-ordering-notice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[Script Info]\n[V4+ Styles]\n[Events]");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");

            var runner = new WarningRunner(output, 1, warningOutput, string.Empty);
            var result = await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output);

            Assert.False(result.HadWarnings);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task KeepsWarningWhenInvalidSubtitleEntryIsSkipped()
    {
        var root = Path.Combine(Path.GetTempPath(), $"submux-batch-invalid-subtitle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var ass = Path.Combine(root, "new.ass");
            var srt = Path.Combine(root, "new.srt");
            var output = Path.Combine(root, "output.mkv");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllTextAsync(ass, "[Script Info]\n[V4+ Styles]\n[Events]");
            await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nx\n");

            const string warning = "SSA/ASS: The following line will be skipped as the end timestamp is less than the start timestamp.";
            var runner = new WarningRunner(output, 1, $"#GUI#warning {warning}\n", string.Empty);
            var result = await new MkvMergeClient("fake-mkvmerge.exe", runner).MuxAsync(
                source,
                ass,
                srt,
                output);

            Assert.True(result.HadWarnings);
            Assert.Equal(warning, Assert.Single(result.Warnings));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class WarningRunner(
        string outputPath,
        int exitCode,
        string standardOutput,
        string standardError) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            await File.WriteAllBytesAsync(outputPath, [1], cancellationToken);
            foreach (var line in ReadLines(standardOutput).Concat(ReadLines(standardError)))
            {
                onOutput?.Invoke(line);
            }

            return new ProcessResult(exitCode, standardOutput, standardError);
        }

        private static IEnumerable<string> ReadLines(string text)
        {
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line)
            {
                yield return line;
            }
        }
    }
}
