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
    public void ExtractsOnlyFacesUsedByDialogueText()
    {
        const string ass = """
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic
            Style: Default,Base Family,40,&H0,&H0,&H0,&H0,-1,0
            Style: Unused,Unused Family,40,&H0,&H0,&H0,&H0,0,0
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Text
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\fnInline Family\b0}More
            Comment: 0,0:00:00.00,0:00:01.00,Unused,,0,0,0,,Ignored
            """;

        var requirements = AssFontNameExtractor.ExtractRequirements(ass);

        Assert.Contains(new AssFontRequirement("Base Family", 700, false), requirements);
        Assert.Contains(new AssFontRequirement("Inline Family", 400, false), requirements);
        Assert.DoesNotContain(requirements, static requirement => requirement.FamilyName == "Unused Family");
    }

    [Fact]
    public void ExcludesBaseFaceWhenInlineFontPrecedesAllText()
    {
        const string ass = """
            [V4+ Styles]
            Format: Name, Fontname, Bold, Italic
            Style: Default,Base Family,0,0
            [Events]
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\fnOther Family}Text
            """;

        var requirement = Assert.Single(AssFontNameExtractor.ExtractRequirements(ass));

        Assert.Equal(new AssFontRequirement("Other Family", 400, false), requirement);
    }

    [Fact]
    public void TracksTransformsResetsAndDrawingModeWithoutInspectingGlyphs()
    {
        const string ass = """
            [V4+ Styles]
            Format: Name, Fontname, Bold, Italic
            Style: Default,Base Family,0,0
            Style: Emphasis,Reset Family,0,0
            [Events]
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\p1}m 0 0 l 1 1{\p0\t(0,500,\fnAnimated Family\b700)}Text{\rEmphasis\i1}More
            """;

        var requirements = AssFontNameExtractor.ExtractRequirements(ass);

        Assert.Contains(new AssFontRequirement("Base Family", 400, false), requirements);
        Assert.Contains(new AssFontRequirement("Animated Family", 700, false), requirements);
        Assert.Contains(new AssFontRequirement("Reset Family", 400, true), requirements);
        Assert.Equal(3, requirements.Count);
    }

    [Fact]
    public void AppliesFnZeroAndInvalidStyleTagsToTheCurrentResetStyle()
    {
        const string ass = """
            [V4+ Styles]
            Format: Name, Fontname, Bold, Italic
            Style: Default,Base Family,0,0
            Style: Other,Other Family,-1,-1
            [Events]
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\rOther\fnTemporary Family}A{\fn0\b-1\i-1}B{\rother}C
            """;

        var requirements = AssFontNameExtractor.ExtractRequirements(ass);

        Assert.Contains(new AssFontRequirement("Temporary Family", 700, true), requirements);
        Assert.Contains(new AssFontRequirement("Other Family", 700, true), requirements);
        Assert.Contains(new AssFontRequirement("Base Family", 400, false), requirements);
        Assert.Equal(3, requirements.Count);
    }

    [Fact]
    public void DirectFaceNameWinsOverAConflictingFamilyName()
    {
        WriteFont(Path.Combine(_root, "family.ttf"), "Example Bold", fullName: "Different Regular");
        WriteFont(
            Path.Combine(_root, "bold.ttf"),
            "Example",
            fullName: "Example Bold",
            postScriptName: "Example-Bold",
            weight: 700);

        var match = new InstalledFontResolver([_root]).Resolve(
            new AssFontRequirement("Example Bold", 400, false));

        Assert.NotNull(match);
        Assert.Equal(InstalledFontMatchKind.DirectFaceName, match.MatchKind);
        Assert.Equal("bold.ttf", match.File.SourceFileName);
        Assert.Equal(700, match.SelectedWeight);
    }

    [Fact]
    public void FamilyMatchSelectsTheRequestedWeightAndSlant()
    {
        WriteFont(Path.Combine(_root, "regular.ttf"), "Face Family", weight: 400);
        WriteFont(Path.Combine(_root, "bold.ttf"), "Face Family", weight: 700);
        WriteFont(Path.Combine(_root, "italic.ttf"), "Face Family", weight: 400, italic: true);

        var resolver = new InstalledFontResolver([_root]);

        Assert.Equal("bold.ttf", resolver.Resolve(new AssFontRequirement("Face Family", 700, false))!.File.SourceFileName);
        Assert.Equal("italic.ttf", resolver.Resolve(new AssFontRequirement("Face Family", 400, true))!.File.SourceFileName);
    }

    [Fact]
    public void ARegularFullNameEqualToTheFamilyDoesNotOverrideRequestedBold()
    {
        WriteFont(Path.Combine(_root, "regular.ttf"), "Common Family", fullName: "Common Family");
        WriteFont(Path.Combine(_root, "bold.ttf"), "Common Family", fullName: "Common Family Bold", weight: 700);

        var match = new InstalledFontResolver([_root]).Resolve(
            new AssFontRequirement("Common Family", 700, false));

        Assert.NotNull(match);
        Assert.Equal(InstalledFontMatchKind.LegacyFamily, match.MatchKind);
        Assert.Equal("bold.ttf", match.File.SourceFileName);
    }

    [Fact]
    public void WwsFamilyIsMatchedBeforeBroaderTypographicFamily()
    {
        WriteFont(
            Path.Combine(_root, "caption.ttf"),
            "Design Caption",
            typographicFamily: "Design",
            wwsFamily: "Design Caption");
        WriteFont(Path.Combine(_root, "other.ttf"), "Design Caption");

        var match = new InstalledFontResolver([_root]).Resolve(
            new AssFontRequirement("Design Caption", 400, false));

        Assert.NotNull(match);
        Assert.Equal(InstalledFontMatchKind.WwsFamily, match.MatchKind);
        Assert.Equal("caption.ttf", match.File.SourceFileName);
    }

    [Fact]
    public void ObliqueOs2FaceSatisfiesAnItalicRequirement()
    {
        WriteFont(Path.Combine(_root, "upright.ttf"), "Slanted Family");
        WriteFont(Path.Combine(_root, "oblique.ttf"), "Slanted Family", oblique: true);

        var match = new InstalledFontResolver([_root]).Resolve(
            new AssFontRequirement("Slanted Family", 400, true));

        Assert.NotNull(match);
        Assert.Equal("oblique.ttf", match.File.SourceFileName);
        Assert.True(match.SelectedItalic);
    }

    [Fact]
    public void RegistryAliasFindsAndIndexesAnExternalFontFile()
    {
        var scanned = Path.Combine(_root, "scanned");
        Directory.CreateDirectory(scanned);
        var external = Path.Combine(_root, "YDOO08.TTF");
        WriteFont(external, "Yj BACDOO", fullName: "Yj BACDOO Bold", weight: 700);
        var resolver = new InstalledFontResolver(
            [scanned],
            [new InstalledFontResolver.RegisteredFontEntry("양재백두체B (TrueType)", external)]);

        var match = resolver.Resolve(new AssFontRequirement("양재백두체B", 700, false));

        Assert.NotNull(match);
        Assert.Equal(InstalledFontMatchKind.RegistryAlias, match.MatchKind);
        Assert.Equal("YDOO08.TTF", match.File.SourceFileName);
        Assert.Equal("Yj BACDOO", match.InternalName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void FindsDefaultMalgunGothicFromWindowsFontMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var match = InstalledFontResolver.System.Resolve(new AssFontRequirement("맑은 고딕", 400, false));

        Assert.NotNull(match);
        Assert.True(File.Exists(match.File.FilePath));
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("맑은 고딕", 400, false, "malgun.ttf")]
    [InlineData("맑은 고딕", 700, false, "malgunbd.ttf")]
    [InlineData("Arial", 400, false, "arial.ttf")]
    [InlineData("Arial", 700, false, "arialbd.ttf")]
    [InlineData("Arial", 400, true, "ariali.ttf")]
    [InlineData("微软雅黑", 400, false, "msyh.ttc")]
    public void SelectsOnlyTheRequestedInstalledFace(
        string familyName,
        int weight,
        bool italic,
        string expectedFileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var expectedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Fonts",
            expectedFileName);
        if (!File.Exists(expectedPath))
        {
            return;
        }

        var match = InstalledFontResolver.System.Resolve(new AssFontRequirement(familyName, weight, italic));

        Assert.NotNull(match);
        Assert.Equal(expectedFileName, match.File.SourceFileName, ignoreCase: true);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("a두리둥실", "A두리둥실.TTF")]
    [InlineData("HY바다L", "HY바다L-YOOND1004.TTF")]
    [InlineData("양재백두체B", "YDOO08.TTF")]
    public void FindsKnownPerUserWindowsFonts(string requestedName, string expectedFileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var expectedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "Windows",
            "Fonts",
            expectedFileName);
        if (!File.Exists(expectedPath))
        {
            return;
        }

        var match = InstalledFontResolver.System.Resolve(new AssFontRequirement(requestedName, 400, false));

        Assert.NotNull(match);
        Assert.Equal(expectedFileName, match.File.SourceFileName, ignoreCase: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteFont(
        string path,
        string legacyFamily,
        string? fullName = null,
        string? postScriptName = null,
        string? typographicFamily = null,
        string? wwsFamily = null,
        ushort weight = 400,
        bool italic = false,
        bool oblique = false)
    {
        var subfamily = weight >= 700 ? "Bold" : "Regular";
        if (italic || oblique)
        {
            subfamily += " Italic";
        }

        var names = new List<(ushort Id, string Value)>
        {
            (1, legacyFamily),
            (2, subfamily),
            (4, fullName ?? $"{legacyFamily} {subfamily}".Trim()),
            (6, postScriptName ?? $"{legacyFamily.Replace(" ", string.Empty, StringComparison.Ordinal)}-{subfamily.Replace(" ", string.Empty, StringComparison.Ordinal)}")
        };
        if (typographicFamily is not null)
        {
            names.Add((16, typographicFamily));
            names.Add((17, subfamily));
        }

        if (wwsFamily is not null)
        {
            names.Add((21, wwsFamily));
            names.Add((22, subfamily));
        }

        var encoded = names.Select(static value => Encoding.BigEndianUnicode.GetBytes(value.Value)).ToArray();
        var stringOffset = 6 + names.Count * 12;
        var nameTable = new byte[stringOffset + encoded.Sum(static value => value.Length)];
        WriteUInt16(nameTable, 2, (ushort)names.Count);
        WriteUInt16(nameTable, 4, (ushort)stringOffset);
        var storageOffset = 0;
        for (var index = 0; index < names.Count; index++)
        {
            var record = 6 + index * 12;
            WriteUInt16(nameTable, record, 3);
            WriteUInt16(nameTable, record + 2, 1);
            WriteUInt16(nameTable, record + 4, 0x0409);
            WriteUInt16(nameTable, record + 6, names[index].Id);
            WriteUInt16(nameTable, record + 8, (ushort)encoded[index].Length);
            WriteUInt16(nameTable, record + 10, (ushort)storageOffset);
            encoded[index].CopyTo(nameTable, stringOffset + storageOffset);
            storageOffset += encoded[index].Length;
        }

        var os2 = new byte[64];
        WriteUInt16(os2, 0, 4);
        WriteUInt16(os2, 4, weight);
        WriteUInt16(os2, 6, 5);
        var selection = (ushort)0;
        if (italic) selection |= 0x0001;
        if (weight >= 700) selection |= 0x0020;
        if (oblique) selection |= 0x0200;
        WriteUInt16(os2, 62, selection);

        var tables = new[] { (Tag: 0x6E616D65u, Data: nameTable), (Tag: 0x4F532F32u, Data: os2) };
        var directoryLength = 12 + tables.Length * 16;
        var bytes = new byte[directoryLength + tables.Sum(static table => table.Data.Length)];
        WriteUInt32(bytes, 0, 0x00010000);
        WriteUInt16(bytes, 4, (ushort)tables.Length);
        var dataOffset = directoryLength;
        for (var index = 0; index < tables.Length; index++)
        {
            var record = 12 + index * 16;
            WriteUInt32(bytes, record, tables[index].Tag);
            WriteUInt32(bytes, record + 8, (uint)dataOffset);
            WriteUInt32(bytes, record + 12, (uint)tables[index].Data.Length);
            tables[index].Data.CopyTo(bytes, dataOffset);
            dataOffset += tables[index].Data.Length;
        }

        File.WriteAllBytes(path, bytes);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
}
