using SubMuxBatch.Core.Domain;

namespace SubMuxBatch.Core.Planning;

public static class ConversionPlanFactory
{
    public static ConversionPlan Create(MediaSet media)
    {
        if (media.HasVideoConflict)
        {
            var candidates = string.Join(", ", media.CandidateVideoPaths.Select(Path.GetFileName));
            return Invalid($"같은 기준 이름의 영상이 여러 개 있습니다: {candidates}");
        }

        if (media.VideoPath is null)
        {
            return Invalid("동일한 파일명의 영상이 없습니다.");
        }

        if (!media.HasAnySubtitle)
        {
            return Invalid("동일한 파일명의 자막(ASS, SRT 또는 SMI)이 없습니다.");
        }

        var warnings = new List<string>();
        if (media.SmiPath is not null && media.SrtPath is not null)
        {
            warnings.Add("SRT가 이미 있으므로 SMI는 사용하지 않습니다.");
        }

        if (media.AssPath is not null && media.SrtPath is not null)
        {
            return new ConversionPlan(
                true,
                AssSourceKind.Existing,
                SrtSourceKind.Existing,
                "기존 ASS + 기존 SRT",
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
                    "기존 ASS + SMI → SRT",
                    warnings);
            }

            return new ConversionPlan(
                true,
                AssSourceKind.Existing,
                SrtSourceKind.ConvertFromAss,
                "기존 ASS + ASS → SRT",
                warnings);
        }

        if (media.SrtPath is not null)
        {
            return new ConversionPlan(
                true,
                AssSourceKind.ConvertFromSrt,
                SrtSourceKind.Existing,
                "SRT → ASS + 기존 SRT",
                warnings);
        }

        return new ConversionPlan(
            true,
            AssSourceKind.ConvertFromSrt,
            SrtSourceKind.ConvertFromSmi,
            "SMI → SRT → ASS",
            warnings);
    }

    private static ConversionPlan Invalid(string error) => new(
        false,
        AssSourceKind.Existing,
        SrtSourceKind.Existing,
        "처리 불가",
        Array.Empty<string>(),
        error);
}
