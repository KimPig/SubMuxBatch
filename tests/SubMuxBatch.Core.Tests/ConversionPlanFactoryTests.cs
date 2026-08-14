using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.Planning;

namespace SubMuxBatch.Core.Tests;

public sealed class ConversionPlanFactoryTests
{
    public static TheoryData<bool, bool, bool, AssSourceKind, SrtSourceKind, string> ValidCases => new()
    {
        { true, false, false, AssSourceKind.Existing, SrtSourceKind.ConvertFromAss, "기존 ASS + ASS → SRT" },
        { false, true, false, AssSourceKind.ConvertFromSrt, SrtSourceKind.Existing, "SRT → ASS + 기존 SRT" },
        { true, true, false, AssSourceKind.Existing, SrtSourceKind.Existing, "기존 ASS + 기존 SRT" },
        { false, false, true, AssSourceKind.ConvertFromSrt, SrtSourceKind.ConvertFromSmi, "SMI → SRT → ASS" },
        { true, false, true, AssSourceKind.Existing, SrtSourceKind.ConvertFromSmi, "기존 ASS + SMI → SRT" },
        { false, true, true, AssSourceKind.ConvertFromSrt, SrtSourceKind.Existing, "SRT → ASS + 기존 SRT" },
        { true, true, true, AssSourceKind.Existing, SrtSourceKind.Existing, "기존 ASS + 기존 SRT" }
    };

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void ResolvesEverySupportedCombination(
        bool hasAss,
        bool hasSrt,
        bool hasSmi,
        AssSourceKind expectedAss,
        SrtSourceKind expectedSrt,
        string expectedDescription)
    {
        var media = CreateMedia(hasVideo: true, hasAss, hasSrt, hasSmi);

        var plan = ConversionPlanFactory.Create(media);

        Assert.True(plan.IsValid);
        Assert.Equal(expectedAss, plan.AssSource);
        Assert.Equal(expectedSrt, plan.SrtSource);
        Assert.Equal(expectedDescription, plan.Description);
        Assert.Equal(hasSrt && hasSmi, plan.Warnings.Count > 0);
    }

    [Fact]
    public void RejectsSubtitleWithoutVideo()
    {
        var plan = ConversionPlanFactory.Create(CreateMedia(false, true, false, false));
        Assert.False(plan.IsValid);
        Assert.Contains("영상", plan.Error);
    }

    [Fact]
    public void RejectsVideoWithoutSubtitle()
    {
        var plan = ConversionPlanFactory.Create(CreateMedia(true, false, false, false));
        Assert.False(plan.IsValid);
        Assert.Contains("자막", plan.Error);
    }

    [Fact]
    public void RejectsMultipleVideosWithTheSameStem()
    {
        var media = new MediaSet(
            new MediaKey(@"C:\Media", "Movie"),
            null,
            null,
            @"C:\Media\Movie.srt",
            null,
            [@"C:\Media\Movie.mkv", @"C:\Media\Movie.mp4"]);

        var plan = ConversionPlanFactory.Create(media);

        Assert.False(plan.IsValid);
        Assert.Contains("Movie.mkv", plan.Error);
        Assert.Contains("Movie.mp4", plan.Error);
    }

    private static MediaSet CreateMedia(bool hasVideo, bool hasAss, bool hasSrt, bool hasSmi) => new(
        new MediaKey(@"C:\Media", "Movie"),
        hasVideo ? @"C:\Media\Movie.mkv" : null,
        hasAss ? @"C:\Media\Movie.ass" : null,
        hasSrt ? @"C:\Media\Movie.srt" : null,
        hasSmi ? @"C:\Media\Movie.smi" : null);
}
