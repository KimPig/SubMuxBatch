using System.Globalization;
using System.Text.Json;

namespace SubMuxBatch.Core.Localization;

public static class CoreText
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> English =
        new(() => Load("en"));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Korean =
        new(() => Load("ko"));

    public static string Get(string key, params object?[] arguments)
    {
        var values = string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "ko",
            StringComparison.OrdinalIgnoreCase)
            ? Korean.Value
            : English.Value;
        var fallback = ReferenceEquals(values, English.Value) ? Korean.Value : English.Value;
        var template = values.TryGetValue(key, out var localized)
            ? localized
            : fallback.TryGetValue(key, out localized)
                ? localized
                : key;
        return arguments.Length == 0
            ? template
            : string.Format(CultureInfo.CurrentCulture, template, arguments);
    }

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var assembly = typeof(CoreText).Assembly;
        var resourceName = $"SubMuxBatch.Core.Localization.CoreStrings.{language}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Missing language resource: {resourceName}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
               ?? throw new InvalidOperationException($"Invalid language resource: {resourceName}");
    }
}
