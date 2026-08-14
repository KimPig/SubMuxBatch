using System.Text;
using SubMuxBatch.Core.External;

namespace SubMuxBatch.Core.Tests;

public sealed class SubtitleCompatibilityNormalizerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SubtitleCompatibilityTests",
        Guid.NewGuid().ToString("N"));

    public SubtitleCompatibilityNormalizerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task NormalizesUppercaseFormattingAndFlattensRubyForAssOnly()
    {
        var source = Path.Combine(_root, "source.srt");
        var output = Path.Combine(_root, "output.srt");
        const string text = """
            1
            00:00:00,000 --> 00:00:01,000
            <FONT COLOR="#FF0000" FACE="Arial" SIZE="42"><B>굵게</B></FONT> <RUBY><RB>漢</RB><RT>かん</RT></RUBY>
            """;
        await File.WriteAllTextAsync(source, text, new UTF8Encoding(false));

        await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(source, output);

        var normalized = await File.ReadAllTextAsync(output);
        Assert.Contains("<font color=\"#FF0000\" face=\"Arial\" size=\"42\"><b>굵게</b></font>", normalized);
        Assert.Contains("漢(かん)", normalized);
        Assert.DoesNotContain("<ruby", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(text, await File.ReadAllTextAsync(source));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
