using System.Globalization;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void CoreResourcesReturnKoreanAndEnglishText()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
            Assert.Equal("처리 불가", CoreText.Get("Plan_Invalid"));

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Cannot process", CoreText.Get("Plan_Invalid"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void LanguageSettingDefaultsToSystemAndRoundTrips()
    {
        Assert.Equal(AppLanguage.System, new AppSettings().Language);
        Assert.Equal(AppLanguage.System, AppSettings.Deserialize("{}").Language);
        Assert.Equal(
            AppLanguage.English,
            AppSettings.Deserialize("""{ "Language": "English" }""").Language);
    }

    [Fact]
    public void CopyPreservesLanguageAndInvalidPersistedValueFallsBackToSystem()
    {
        var copy = new AppSettings { Language = AppLanguage.Korean }.Copy();
        var invalid = AppSettings.Deserialize("""{ "Language": 99 }""");

        Assert.Equal(AppLanguage.Korean, copy.Language);
        Assert.Equal(AppLanguage.System, invalid.Language);
    }

    [Theory]
    [InlineData(AppLanguage.System, "ko-KR", "ko-KR")]
    [InlineData(AppLanguage.System, "ja-JP", "en-US")]
    [InlineData(AppLanguage.System, "en-GB", "en-US")]
    [InlineData(AppLanguage.Korean, "en-US", "ko-KR")]
    [InlineData(AppLanguage.English, "ko-KR", "en-US")]
    public void ResolvesSystemDefaultAndExplicitLanguage(
        AppLanguage language,
        string systemCulture,
        string expectedCulture)
    {
        var resolved = AppLanguageResolver.Resolve(
            language,
            CultureInfo.GetCultureInfo(systemCulture));

        Assert.Equal(expectedCulture, resolved.Name);
    }
}
