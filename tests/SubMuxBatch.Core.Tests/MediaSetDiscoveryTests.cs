using SubMuxBatch.Core.Discovery;
using SubMuxBatch.Core.Planning;

namespace SubMuxBatch.Core.Tests;

public sealed class MediaSetDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SubMuxBatchTests", Guid.NewGuid().ToString("N"));

    public MediaSetDiscoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DroppingOneFileFindsExactStemCompanions()
    {
        var mkv = Touch("Movie.mkv");
        Touch("Movie.ass");
        Touch("Movie.srt");
        Touch("Movie.ko.srt");

        var result = await new MediaSetDiscovery().DiscoverAsync([mkv], false);

        var media = Assert.Single(result);
        Assert.EndsWith("Movie.mkv", media.VideoPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Movie.ass", media.AssPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Movie.srt", media.SrtPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result, item => item.Key.Stem == "Movie.ko");
    }

    [Fact]
    public async Task SameStemInDifferentFoldersNeverMerges()
    {
        var first = Directory.CreateDirectory(Path.Combine(_root, "A")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "B")).FullName;
        File.WriteAllBytes(Path.Combine(first, "01.mkv"), [1]);
        File.WriteAllText(Path.Combine(second, "01.ass"), "x");

        var result = await new MediaSetDiscovery().DiscoverAsync([first, second], false);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.VideoPath is not null && item.AssPath is null);
        Assert.Contains(result, item => item.VideoPath is null && item.AssPath is not null);
    }

    [Fact]
    public async Task RecursiveScanIncludesPrefixNamedMkvFiles()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "Child")).FullName;
        File.WriteAllBytes(Path.Combine(child, "Episode.mkv"), [1]);
        File.WriteAllText(Path.Combine(child, "Episode.srt"), "x");
        File.WriteAllBytes(Path.Combine(child, "SubMux_Episode.mkv"), [1]);

        var result = await new MediaSetDiscovery().DiscoverAsync([_root], true);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Key.Stem == "Episode");
        Assert.Contains(result, item => item.Key.Stem == "SubMux_Episode");
    }

    [Theory]
    [InlineData(".submuxbatch-deadbeef")]
    [InlineData(".subtitlebatch-deadbeef")]
    public async Task RecursiveScanSkipsAbandonedWorkspaces(string workspaceName)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, workspaceName)).FullName;
        File.WriteAllBytes(Path.Combine(workspace, "output.partial.mkv"), [1]);
        File.WriteAllText(Path.Combine(workspace, "output.partial.srt"), "x");
        Touch("Movie.mkv");
        Touch("Movie.ass");

        var result = await new MediaSetDiscovery().DiscoverAsync([_root], true);

        var media = Assert.Single(result);
        Assert.Equal("Movie", media.Key.Stem);
    }

    [Fact]
    public async Task SuffixMatchingIsOptIn()
    {
        var mkv = Touch("Movie.mkv");
        Touch("Movie.kor.srt");

        var result = await new MediaSetDiscovery().DiscoverAsync([mkv], false);

        var media = Assert.Single(result);
        Assert.Null(media.SrtPath);
    }

    [Fact]
    public async Task DroppingMkvSelectsPreferredSubtitleSuffixPerExtension()
    {
        var mkv = Touch("Movie.mkv");
        Touch("Movie.commentary.ass");
        Touch("Movie.ko.ass");
        Touch("Movie.kor.long.ass");
        Touch("Movie.short.srt");
        Touch("Movie.kor.long.srt");
        Touch("Movie.ko.srt");
        Touch("Movie.smi");
        Touch("Movie.ko.smi");

        var result = await new MediaSetDiscovery(allowSubtitleSuffixMatch: true)
            .DiscoverAsync([mkv], false);

        var media = Assert.Single(result);
        Assert.EndsWith("Movie.ko.ass", media.AssPath, StringComparison.Ordinal);
        Assert.EndsWith("Movie.ko.srt", media.SrtPath, StringComparison.Ordinal);
        Assert.EndsWith("Movie.smi", media.SmiPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactSubtitleWinsOverAllSuffixCandidates()
    {
        var mkv = Touch("Movie.mkv");
        Touch("Movie.srt");
        Touch("Movie.ko.srt");
        Touch("Movie.kor.srt");

        var result = await new MediaSetDiscovery(allowSubtitleSuffixMatch: true)
            .DiscoverAsync([mkv], false);

        var media = Assert.Single(result);
        Assert.EndsWith("Movie.srt", media.SrtPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KoreanSuffixWinsBeforeShorterNonLanguageSuffix()
    {
        var mkv = Touch("Movie.mkv");
        Touch("Movie.a.srt");
        Touch("Movie.kor.release.srt");

        var result = await new MediaSetDiscovery(allowSubtitleSuffixMatch: true)
            .DiscoverAsync([mkv], false);

        var media = Assert.Single(result);
        Assert.EndsWith("Movie.kor.release.srt", media.SrtPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EqualLengthSuffixCandidatesUseOrdinalOrder()
    {
        var mkv = Touch("Movie.mkv");
        Touch("Movie.zz.srt");
        Touch("Movie.aa.srt");

        var result = await new MediaSetDiscovery(allowSubtitleSuffixMatch: true)
            .DiscoverAsync([mkv], false);

        var media = Assert.Single(result);
        Assert.EndsWith("Movie.aa.srt", media.SrtPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubtitleUsesLongestMatchingMkvStemAndDropAddsOnlyThatBundle()
    {
        Touch("Movie.mkv");
        var expectedMkv = Touch("Movie.Special.mkv");
        var subtitle = Touch("Movie.Special.kor.srt");
        Touch("Unrelated.mkv");
        Touch("Unrelated.srt");

        var result = await new MediaSetDiscovery(allowSubtitleSuffixMatch: true)
            .DiscoverAsync([subtitle], false);

        var media = Assert.Single(result);
        Assert.Equal(Path.GetFullPath(expectedMkv), media.VideoPath);
        Assert.Equal(Path.GetFullPath(subtitle), media.SrtPath);
        Assert.Equal("Movie.Special", media.Key.Stem);
    }

    [Fact]
    public async Task MultipleExplicitFilesInOneFolderReturnOnlyTheirRelatedBundles()
    {
        var movieMkv = Touch("Movie.mkv");
        var movieSubtitle = Touch("Movie.ko.srt");
        var episodeSubtitle = Touch("Episode.kor.ass");
        Touch("Episode.mkv");
        Touch("Unrelated.mkv");
        Touch("Unrelated.srt");

        var result = await new MediaSetDiscovery(allowSubtitleSuffixMatch: true)
            .DiscoverAsync([movieMkv, movieSubtitle, episodeSubtitle], false);

        Assert.Equal(2, result.Count);
        var movie = Assert.Single(result, media => media.Key.Stem == "Movie");
        Assert.EndsWith("Movie.ko.srt", movie.SrtPath, StringComparison.Ordinal);
        var episode = Assert.Single(result, media => media.Key.Stem == "Episode");
        Assert.EndsWith("Episode.kor.ass", episode.AssPath, StringComparison.Ordinal);
        Assert.DoesNotContain(result, media => media.Key.Stem == "Unrelated");
    }

    [Fact]
    public async Task FolderScanGroupsSuffixesWithoutCreatingOrphanRows()
    {
        Touch("Movie.mkv");
        Touch("Movie.kor.ass");
        Touch("Movie.ko.srt");
        Touch("Other.mkv");
        Touch("Other.srt");

        var result = await new MediaSetDiscovery(allowSubtitleSuffixMatch: true)
            .DiscoverAsync([_root], false);

        Assert.Equal(2, result.Count);
        var movie = Assert.Single(result, media => media.Key.Stem == "Movie");
        Assert.EndsWith("Movie.kor.ass", movie.AssPath, StringComparison.Ordinal);
        Assert.EndsWith("Movie.ko.srt", movie.SrtPath, StringComparison.Ordinal);
        Assert.DoesNotContain(result, media => media.Key.Stem is "Movie.kor" or "Movie.ko");
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".mp4")]
    [InlineData(".m4v")]
    [InlineData(".mov")]
    [InlineData(".avi")]
    [InlineData(".ts")]
    [InlineData(".mts")]
    [InlineData(".m2ts")]
    [InlineData(".webm")]
    [InlineData(".MP4")]
    public async Task EverySupportedVideoExtensionFindsExactStemSubtitle(string extension)
    {
        var video = Touch("Format" + extension);
        var subtitle = Touch("Format.srt");

        var result = await new MediaSetDiscovery().DiscoverAsync([video], false);

        var media = Assert.Single(result);
        Assert.Equal(Path.GetFullPath(video), media.VideoPath, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(subtitle), media.SrtPath, ignoreCase: true);
    }

    [Fact]
    public async Task SubtitleSuffixUsesLongestMatchingVideoStemAcrossContainers()
    {
        Touch("Movie.mp4");
        var expectedVideo = Touch("Movie.Special.webm");
        var subtitle = Touch("Movie.Special.ko.srt");

        var result = await new MediaSetDiscovery(allowSubtitleSuffixMatch: true)
            .DiscoverAsync([subtitle], false);

        var media = Assert.Single(result);
        Assert.Equal(Path.GetFullPath(expectedVideo), media.VideoPath);
        Assert.Equal(Path.GetFullPath(subtitle), media.SrtPath);
    }

    [Fact]
    public async Task SameStemVideosAreReportedAsOneConflictRegardlessOfInputOrder()
    {
        var mkv = Touch("Duplicate.mkv");
        var mp4 = Touch("Duplicate.mp4");
        Touch("Duplicate.srt");
        var discovery = new MediaSetDiscovery();

        var forward = Assert.Single(await discovery.DiscoverAsync([mkv, mp4], false));
        var reverse = Assert.Single(await discovery.DiscoverAsync([mp4, mkv], false));

        Assert.True(forward.HasVideoConflict);
        Assert.True(reverse.HasVideoConflict);
        Assert.Null(forward.VideoPath);
        Assert.Equal(forward.CandidateVideoPaths, reverse.CandidateVideoPaths);
    }

    [Fact]
    public async Task ConflictRecoversAfterCandidateIsRemovedAndRediscovered()
    {
        var mkv = Touch("Recover.mkv");
        var mp4 = Touch("Recover.mp4");
        Touch("Recover.srt");
        var discovery = new MediaSetDiscovery();
        var conflict = Assert.Single(await discovery.DiscoverAsync([mkv, mp4], false));
        File.Delete(mp4);

        var refreshed = Assert.Single(await discovery.DiscoverAsync([mkv], false));
        var merged = conflict.Merge(refreshed);

        Assert.False(merged.HasVideoConflict);
        Assert.Equal(Path.GetFullPath(mkv), merged.VideoPath);
        Assert.Single(merged.CandidateVideoPaths);
    }
    [Fact]
    public async Task ConflictClearsAfterAllCandidatesAreRemovedAndRediscovered()
    {
        var mkv = Touch("Removed.mkv");
        var mp4 = Touch("Removed.mp4");
        var subtitle = Touch("Removed.srt");
        var discovery = new MediaSetDiscovery();
        var conflict = Assert.Single(await discovery.DiscoverAsync([mkv, mp4], false));
        File.Delete(mkv);
        File.Delete(mp4);

        var refreshed = Assert.Single(await discovery.DiscoverAsync([subtitle], false));
        var merged = conflict.Merge(refreshed);

        Assert.False(merged.HasVideoConflict);
        Assert.Null(merged.VideoPath);
        Assert.Empty(merged.CandidateVideoPaths);
        Assert.Contains("영상", ConversionPlanFactory.Create(merged).Error);
    }
    [Fact]
    public async Task PrefixNamedVideosAreAlwaysInputsRegardlessOfContainer()
    {
        var video = Touch("SubMux_Movie.mp4");
        Touch("SubMux_Movie.srt");
        Touch("SubMux_Skipped.mkv");

        var result = await new MediaSetDiscovery().DiscoverAsync([_root], false);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, media => media.VideoPath == Path.GetFullPath(video));
        Assert.Contains(result, media => media.VideoPath == Path.GetFullPath(Path.Combine(_root, "SubMux_Skipped.mkv")));
    }
    private string Touch(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
