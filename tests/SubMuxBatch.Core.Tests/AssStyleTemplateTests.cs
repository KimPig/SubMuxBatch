using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Tests;

public sealed class AssStyleTemplateTests
{
    [Fact]
    public void DefaultsToRequestedRawStyleAt1080p()
    {
        var settings = new AppSettings();

        Assert.True(settings.UseCustomAssStyle);
        Assert.Equal(1920, settings.PlayResX);
        Assert.Equal(1080, settings.PlayResY);
        Assert.False(settings.RemoveExistingFontAttachments);
        Assert.False(settings.RemoveChapters);
        Assert.True(settings.AttachAssStyleFonts);
        Assert.Equal(
            "Style: Default,맑은 고딕,79.5,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,100,0.0,0,1,2.3,3.8,2,30,30,77,1",
            settings.AssStyleLine);
    }

    [Fact]
    public void WriterWrapsParsedStyleInCanonicalForm()
    {
        const string rawStyle =
            "Style: Default,Test Font,53.500,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,100,0.00,0,1,1.50,2.500,2,20,20,51,1";
        var settings = new AppSettings
        {
            PlayResX = 1280,
            PlayResY = 720,
            AssStyleLine = rawStyle
        };

        var text = AssStyleTemplateWriter.Create(settings);
        var normalizedText = text.ReplaceLineEndings("\n");

        Assert.Contains("PlayResX: 1280", text);
        Assert.Contains("PlayResY: 720", text);
        const string canonicalStyle =
            "Style: Default,Test Font,53.500,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,100,0.00,0,1,1.50,2.500,2,20,20,51,1";
        Assert.Contains($"\n{canonicalStyle}\n", normalizedText);
        Assert.Equal(1, Count(text, canonicalStyle));
    }

    [Fact]
    public void WriterAcceptsStyleBodyWithoutPrefixAndCanonicalizesIt()
    {
        const string styleBody =
            "Default,Test Font,53.5,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,100,0,0,1,1.5,2.5,2,20,20,51,1";
        var settings = new AppSettings { AssStyleLine = styleBody };

        settings.Validate();
        var text = AssStyleTemplateWriter.Create(settings);
        var normalizedText = text.ReplaceLineEndings("\n");

        Assert.Contains($"\nStyle: {styleBody}\n", normalizedText);
    }

    [Fact]
    public void SettingsRejectsAValidNonDefaultStyleName()
    {
        var settings = new AppSettings
        {
            AssStyleLine = AppSettings.DefaultAssStyleLine.Replace(
                "Style: Default,",
                "Style: Dialogue,",
                StringComparison.Ordinal)
        };

        var error = Assert.Throws<InvalidOperationException>(settings.Validate);

        Assert.Contains("Default", error.Message);
    }

    [Fact]
    public void WriterRunsFullSettingsValidationBeforeCreatingTemplate()
    {
        var invalidName = new AppSettings
        {
            AssStyleLine = AppSettings.DefaultAssStyleLine.Replace(
                "Style: Default,",
                "Style: Dialogue,",
                StringComparison.Ordinal)
        };
        var invalidResolution = new AppSettings { PlayResX = 0 };
        var disabledStyle = new AppSettings
        {
            UseCustomAssStyle = false,
            AssStyleLine = string.Empty
        };

        Assert.Throws<InvalidOperationException>(() => AssStyleTemplateWriter.Create(invalidName));
        Assert.Throws<InvalidOperationException>(() => AssStyleTemplateWriter.Create(invalidResolution));
        Assert.Throws<FormatException>(() => AssStyleTemplateWriter.Create(disabledStyle));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Style: Other,Arial,20")]
    [InlineData("Style: Default,Arial,20")]
    [InlineData("Style: Default,Arial,20\nStyle: Other,Arial,20")]
    public void EnabledCustomStyleRejectsMalformedRawLine(string styleLine)
    {
        var settings = new AppSettings { AssStyleLine = styleLine };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void DisabledCustomStyleAllowsAnEmptyRawLine()
    {
        var settings = new AppSettings
        {
            UseCustomAssStyle = false,
            AssStyleLine = string.Empty
        };

        settings.Validate();
    }

    [Fact]
    public void CopyKeepsSimplifiedAssSettings()
    {
        var settings = new AppSettings
        {
            UseCustomAssStyle = false,
            PlayResX = 1280,
            PlayResY = 720,
            RemoveExistingFontAttachments = true,
            RemoveChapters = true,
            AttachAssStyleFonts = false,
            AssStyleLine = "saved for later"
        };

        var copy = settings.Copy();

        Assert.False(copy.UseCustomAssStyle);
        Assert.Equal(1280, copy.PlayResX);
        Assert.Equal(720, copy.PlayResY);
        Assert.True(copy.RemoveExistingFontAttachments);
        Assert.True(copy.RemoveChapters);
        Assert.False(copy.AttachAssStyleFonts);
        Assert.Equal("saved for later", copy.AssStyleLine);
    }

    [Fact]
    public void LegacyJsonUsesNewStyleDefaultsAndPreservesPlayRes()
    {
        const string legacy = """
            {
              "AssFontName": "Custom",
              "AssFontSize": 40,
              "AssOutline": 3,
              "PlayResX": 1280,
              "PlayResY": 720
            }
            """;

        var migrated = AppSettings.Deserialize(legacy);

        Assert.True(migrated.UseCustomAssStyle);
        Assert.Equal(1280, migrated.PlayResX);
        Assert.Equal(720, migrated.PlayResY);
        Assert.Equal(AppSettings.DefaultAssStyleLine, migrated.AssStyleLine);
        Assert.True(migrated.AttachAssStyleFonts);
        Assert.False(migrated.RemoveChapters);
    }

    private static int Count(string value, string pattern) =>
        (value.Length - value.Replace(pattern, string.Empty, StringComparison.Ordinal).Length) / pattern.Length;
}
