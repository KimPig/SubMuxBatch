using System.Text;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class MediaInfoClientTests
{
    [Fact]
    public void InspectReadsDurationAndAudioDetailsFromContentWithWrongExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "SubMuxBatch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "audio.mkv");

        try
        {
            WriteOneSecondWave(path);

            var inspection = new MediaInfoClient().Inspect(path);

            Assert.Contains("Wave", inspection.ContainerFormat, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(inspection.DurationNanoseconds ?? 0, 990_000_000L, 1_010_000_000L);
            Assert.Equal(new FileInfo(path).Length, inspection.FileSizeBytes);
            var audio = Assert.Single(inspection.AudioStreams);
            Assert.Equal(1, audio.Channels);
            Assert.Equal(8_000d, audio.SamplingRate);
            Assert.Equal(16, audio.BitDepth);
            Assert.InRange(audio.Bitrate ?? 0, 127_000L, 129_000L);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectRejectsMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.mkv");

        var exception = Assert.Throws<InvalidOperationException>(() => new MediaInfoClient().Inspect(path));

        Assert.Contains("missing.mkv", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteOneSecondWave(string path)
    {
        const short channels = 1;
        const int sampleRate = 8_000;
        const short bitsPerSample = 16;
        const int sampleCount = sampleRate;
        const short blockAlign = channels * bitsPerSample / 8;
        const int byteRate = sampleRate * blockAlign;
        const int dataSize = sampleCount * blockAlign;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
    }
}
