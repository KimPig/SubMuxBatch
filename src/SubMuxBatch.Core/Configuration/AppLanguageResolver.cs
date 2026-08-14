using System.Globalization;

namespace SubMuxBatch.Core.Configuration;

public static class AppLanguageResolver
{
    public static CultureInfo Resolve(AppLanguage language, CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);

        return language switch
        {
            AppLanguage.Korean => CultureInfo.GetCultureInfo("ko-KR"),
            AppLanguage.English => CultureInfo.GetCultureInfo("en-US"),
            _ when string.Equals(
                systemCulture.TwoLetterISOLanguageName,
                "ko",
                StringComparison.OrdinalIgnoreCase) => CultureInfo.GetCultureInfo("ko-KR"),
            _ => CultureInfo.GetCultureInfo("en-US")
        };
    }
}
