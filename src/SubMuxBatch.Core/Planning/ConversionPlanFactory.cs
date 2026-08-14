using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Planning;

public static class ConversionPlanFactory
{
    public static ConversionPlan Create(MediaSet media)
    {
        if (media.HasVideoConflict)
        {
            var candidates = string.Join(", ", media.CandidateVideoPaths.Select(Path.GetFileName));
            return Invalid(CoreText.Get("Plan_VideoConflict", candidates));
        }

        if (media.VideoPath is null)
        {
            return Invalid(CoreText.Get("Plan_NoVideo"));
        }

        if (!media.HasAnySubtitle)
        {
            return Invalid(CoreText.Get("Plan_NoSubtitle"));
        }

        var warnings = new List<string>();
        if (media.SmiPath is not null && media.SrtPath is not null)
        {
            warnings.Add(CoreText.Get("Plan_IgnoreSmi"));
        }

        if (media.AssPath is not null && media.SrtPath is not null)
        {
            return new ConversionPlan(
                true,
                AssSourceKind.Existing,
                SrtSourceKind.Existing,
                CoreText.Get("Plan_ExistingAssSrt"),
                warnings);
        }

        if (media.AssPath is not null)
        {
            if (media.SmiPath is not null)
            {
                return new ConversionPlan(
                    true,
                    AssSourceKind.Existing,
                    SrtSourceKind.ConvertFromSmi,
                    CoreText.Get("Plan_ExistingAssSmiToSrt"),
                    warnings);
            }

            return new ConversionPlan(
                true,
                AssSourceKind.Existing,
                SrtSourceKind.ConvertFromAss,
                CoreText.Get("Plan_ExistingAssToSrt"),
                warnings);
        }

        if (media.SrtPath is not null)
        {
            return new ConversionPlan(
                true,
                AssSourceKind.ConvertFromSrt,
                SrtSourceKind.Existing,
                CoreText.Get("Plan_SrtToAss"),
                warnings);
        }

        return new ConversionPlan(
            true,
            AssSourceKind.ConvertFromSrt,
            SrtSourceKind.ConvertFromSmi,
            CoreText.Get("Plan_SmiToSrtToAss"),
            warnings);
    }

    private static ConversionPlan Invalid(string error) => new(
        false,
        AssSourceKind.Existing,
        SrtSourceKind.Existing,
        CoreText.Get("Plan_Invalid"),
        Array.Empty<string>(),
        error);
}
