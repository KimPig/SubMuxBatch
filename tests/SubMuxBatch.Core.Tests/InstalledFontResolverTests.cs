using System.Buffers.Binary;
using System.Text;
using SubMuxBatch.Core.Fonts;

namespace SubMuxBatch.Core.Tests;

public sealed class InstalledFontResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SubMuxBatchFontResolverTests",
        Guid.NewGuid().ToString("N"));

    public InstalledFontResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MatchesInternalFamilyMetadataAndReturnsAvailableVariants()
    {
        WriteMinimalFont(Path.Combine(_root, "unrelated-name.ttf"), "테스트 글꼴");
        WriteMinimalFont(Path.Combine(_root, "bold-file.ttf"), "테스트 글꼴");
        WriteMinimalFont(Path.Combine(_root, "italic-file.otf"), "테스트 글꼴");
        WriteMinimalFont(Path.Combine(_root, "different.ttf"), "다른 글꼴");

        var matches = new InstalledFontResolver([_root]).FindByFamilyName("  테스트   글꼴 ");

        Assert.Equal(3, matches.Count);
        Assert.Contains(matches, static font => font.FileName == "unrelated-name.ttf" && font.MimeType == "font/ttf");
        Assert.Contains(matches, static font => font.FileName == "bold-file.ttf" && font.MimeType == "font/ttf");
        Assert.Contains(matches, static font => font.FileName == "italic-file.otf" && font.MimeType == "font/otf");
        Assert.DoesNotContain(matches, static font => font.FileName == "different.ttf");
    }

    [Fact]
    public void ExtractsStyleAndInlineOverrideFontNames()
    {
        const string ass = """
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, Bold, Italic
            Style: Default,Family One,40,-1,0
            Style: Signs,Family Two,30,0,-1
            [Events]
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\fnInline Family}Text
            """;

        var names = AssFontNameExtractor.Extract(ass);

        Assert.Equal(["Family One", "Family Two", "Inline Family"], names);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void FindsDefaultMalgunGothicFromWindowsFontMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var matches = InstalledFontResolver.System.FindByFamilyName("맑은 고딕");

        Assert.NotEmpty(matches);
        Assert.All(matches, static font => Assert.True(File.Exists(font.FilePath)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteMinimalFont(string path, string familyName)
    {
        var nameBytes = Encoding.BigEndianUnicode.GetBytes(familyName);
        var nameTableLength = 18 + nameBytes.Length;
        var bytes = new byte[28 + nameTableLength];

        WriteUInt32(bytes, 0, 0x00010000);
        WriteUInt16(bytes, 4, 1);
        WriteUInt32(bytes, 12, 0x6E616D65); // name
        WriteUInt32(bytes, 20, 28);
        WriteUInt32(bytes, 24, (uint)nameTableLength);

        const int table = 28;
        WriteUInt16(bytes, table, 0);
        WriteUInt16(bytes, table + 2, 1);
        WriteUInt16(bytes, table + 4, 18);
        WriteUInt16(bytes, table + 6, 3);
        WriteUInt16(bytes, table + 8, 1);
        WriteUInt16(bytes, table + 10, 0x0412);
        WriteUInt16(bytes, table + 12, 1);
        WriteUInt16(bytes, table + 14, (ushort)nameBytes.Length);
        WriteUInt16(bytes, table + 16, 0);
        nameBytes.CopyTo(bytes, table + 18);

        File.WriteAllBytes(path, bytes);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
}
