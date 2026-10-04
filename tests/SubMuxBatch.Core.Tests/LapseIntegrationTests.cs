using System.Text.Json;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class LapseIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"SubMuxBatch-Lapse-{Guid.NewGuid():N}");

    [Fact]
    public void ReferenceSelectorUsesDefaultSubtitleRegardlessOfConfiguredAudioLanguage()
    {
        var inspection = new MkvInspection(
        [
            new MkvTrackInfo("video", "V_MPEGH/ISO/HEVC", true, false, null, null, null),
            new MkvTrackInfo("audio", "A_AAC", true, false, "jpn", "ja", null),
            new MkvTrackInfo("subtitles", "S_TEXT/UTF8", true, false, "eng", "en", "English"),
            new MkvTrackInfo("subtitles", "S_TEXT/UTF8", false, false, "jpn", "ja", "Japanese")
        ], [], 0);
        var settings = new AppSettings
        {
            FilterAudioTracksByLanguage = true,
            SelectedAudioLanguage = AudioTrackLanguage.Japanese,
            LapseReference = LapseReferenceMode.Auto
        };

        var selected = LapseReferenceSelector.Select(inspection, settings);

        Assert.Equal(0, selected.SubtitleOrdinal);
        Assert.Equal(0, selected.AudioOrdinal);
        Assert.False(selected.AudioOnly);
    }

    [Fact]
    public void ReferenceSelectorExcludesSignsAndFallsBackToMatchingAudio()
    {
        var inspection = new MkvInspection(
        [
            new MkvTrackInfo("audio", "A_OPUS", false, false, "eng", "en", null),
            new MkvTrackInfo("audio", "A_OPUS", true, false, "jpn", "ja", null),
            new MkvTrackInfo("subtitles", "S_TEXT/ASS", true, false, "jpn", "ja", "Signs & Songs")
        ], [], 0);
        var settings = new AppSettings
        {
            FilterAudioTracksByLanguage = true,
            SelectedAudioLanguage = AudioTrackLanguage.Japanese,
            LapseReference = LapseReferenceMode.Auto
        };

        var selected = LapseReferenceSelector.Select(inspection, settings);

        Assert.Equal(1, selected.AudioOrdinal);
        Assert.True(selected.AudioOnly);
    }

    [Fact]
    public void ReferenceSelectorExcludesForcedAndHearingImpairedDefaultTracks()
    {
        var inspection = new MkvInspection(
        [
            new MkvTrackInfo("audio", "A_AAC", true, false, "jpn", "ja", null),
            new MkvTrackInfo("subtitles", "S_TEXT/UTF8", true, true, "eng", "en", "Forced"),
            new MkvTrackInfo(
                "subtitles", "S_TEXT/UTF8", true, false, "eng", "en", "SDH",
                HearingImpaired: true),
            new MkvTrackInfo("subtitles", "S_TEXT/UTF8", false, false, "eng", "en", "Full dialogue")
        ], [], 0);

        var selected = LapseReferenceSelector.Select(inspection, new AppSettings());

        Assert.Equal(2, selected.SubtitleOrdinal);
        Assert.False(selected.AudioOnly);
    }

    [Fact]
    public void ReferenceSelectorUsesAudioOnlyWhenEmbeddedReferenceIsDisabled()
    {
        var inspection = new MkvInspection(
        [
            new MkvTrackInfo("audio", "A_AAC", true, false, "jpn", "ja", null),
            new MkvTrackInfo("subtitles", "S_TEXT/UTF8", true, false, "eng", "en", "English")
        ], [], 0);

        var selected = LapseReferenceSelector.Select(
            inspection,
            new AppSettings { LapseReference = LapseReferenceMode.AudioOnly });

        Assert.Null(selected.SubtitleOrdinal);
        Assert.Equal(0, selected.AudioOrdinal);
        Assert.True(selected.AudioOnly);
    }

    [Fact]
    public void ReferenceSelectorExcludesManagedSubMuxTracksWithoutChangingMkvOrdinal()
    {
        var inspection = new MkvInspection(
        [
            new MkvTrackInfo("audio", "A_AAC", true, false, "jpn", "ja", null, 0),
            new MkvTrackInfo("subtitles", "S_TEXT/ASS", true, false, "jpn", "ja", "Styled subtitles (ASS)", 2),
            new MkvTrackInfo("subtitles", "S_TEXT/UTF8", false, false, "jpn", "ja", "Reference", 3)
        ], [], 0);
        var settings = new AppSettings
        {
            FilterAudioTracksByLanguage = true,
            SelectedAudioLanguage = AudioTrackLanguage.Japanese,
            LapseReference = LapseReferenceMode.Auto
        };

        var selected = LapseReferenceSelector.Select(inspection, settings, new HashSet<int> { 2 });

        Assert.Equal(1, selected.SubtitleOrdinal);
        Assert.False(selected.AudioOnly);
    }

    [Fact]
    public void ReferenceSelectorFallsBackToAudioWhenEverySubtitleIsManagedBySubMux()
    {
        var inspection = new MkvInspection(
        [
            new MkvTrackInfo("audio", "A_AAC", true, false, "jpn", "ja", null, 0),
            new MkvTrackInfo("subtitles", "S_TEXT/ASS", true, false, "jpn", "ja", "Styled subtitles (ASS)", 2),
            new MkvTrackInfo("subtitles", "S_TEXT/UTF8", false, false, "jpn", "ja", "Standard subtitles (SRT)", 3)
        ], [], 0);
        var settings = new AppSettings
        {
            FilterAudioTracksByLanguage = true,
            SelectedAudioLanguage = AudioTrackLanguage.Japanese,
            LapseReference = LapseReferenceMode.Auto
        };

        var selected = LapseReferenceSelector.Select(inspection, settings, new HashSet<int> { 2, 3 });

        Assert.Equal(0, selected.AudioOrdinal);
        Assert.True(selected.AudioOnly);
    }

    [Fact]
    public void FindsSubMuxManagedSubtitleTracksByTheirReservedTrackNames()
    {
        var inspection = new MkvInspection(
        [
            new MkvTrackInfo("subtitles", "S_TEXT/ASS", true, false, "kor", "ko", "스타일 자막 (ASS)", 2),
            new MkvTrackInfo("subtitles", "S_TEXT/UTF8", false, false, "kor", "ko", "일반 자막 (SRT)", 3),
            new MkvTrackInfo("subtitles", "S_TEXT/UTF8", false, false, "eng", "en", "English", 4)
        ], [], 0);

        var ids = LapseReferenceSelector.FindSubMuxManagedSubtitleTrackIds(inspection);

        Assert.Equal(new[] { 2, 3 }, ids.Order().ToArray());
    }

    [Fact]
    public async Task BundledProviderExtractsEveryLapseFileAndRepairsCorruption()
    {
        Directory.CreateDirectory(_root);
        var provider = CreateProvider();

        var executable = await provider.EnsureAvailableAsync();

        Assert.True(File.Exists(executable));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "onnxruntime.dll")));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "silero_vad.onnx")));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "LICENSE.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "FFmpeg-LGPL-2.1-or-later.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "FFTW-GPL-2.0-or-later.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "libfvad-BSD-3-Clause.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "zlib-License.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "ONNX-Runtime-MIT.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "ONNX-Runtime-ThirdPartyNotices.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "lapse", "Silero-VAD-MIT.txt")));
        await File.WriteAllTextAsync(executable, "corrupted");

        await provider.EnsureAvailableAsync();

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(executable));
    }

    [Fact]
    public void BundledLapseLicenseAndNoticeResourcesAreEmbedded()
    {
        var assembly = typeof(BundledLapseProvider).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        Assert.Contains("SubMuxBatch.Core.Resources.lapse.LICENSE.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.lapse.FFmpeg-LGPL-2.1-or-later.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.lapse.FFTW-GPL-2.0-or-later.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.lapse.libfvad-BSD-3-Clause.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.lapse.zlib-License.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.lapse.ONNX-Runtime-MIT.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.lapse.ONNX-Runtime-ThirdPartyNotices.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.lapse.Silero-VAD-MIT.txt", resourceNames);
    }

    [Fact]
    public void SrtMarkerIsPlacedAfterMediaAndRemovedLosslessly()
    {
        const string source = "1\r\n00:00:01,000 --> 00:00:02,000\r\nText\r\n";

        var marked = LapseSubtitleMetadata.AddSrtMarker(
            source,
            5_000_000_000,
            "2026.10.03",
            "AUTO|AUTO|6|8",
            "embedded");

        Assert.Contains("00:00:05,001 --> 00:00:05,002", marked);
        Assert.Contains("__SUBMUX_LAPSE_SYNC_2026.10.03_LAPSE_2.2.4_PROFILE_AUTO-AUTO-6-8_REF_EMBEDDED__", marked);
        Assert.True(LapseSubtitleMetadata.HasSrtMarker(marked));
        var marker = Assert.IsType<LapseSubtitleMetadata.SrtMarkerInfo>(LapseSubtitleMetadata.ReadSrtMarker(marked));
        Assert.Equal("AUTO|AUTO|6|8", marker.SettingsProfile);
        Assert.Equal("EMBEDDED", marker.Reference);
        Assert.Equal(source, LapseSubtitleMetadata.RemoveSrtMarkers(marked));
    }

    [Fact]
    public void LegacySrtMarkerRemainsReadable()
    {
        const string text = "1\n00:00:01,000 --> 00:00:02,000\nText\n\n"
                            + "2\n00:00:03,000 --> 00:00:03,001\n"
                            + "<font face=\"__SUBMUX_LAPSE_SYNC_2026.10.03_LAPSE_2.2.4__\">⁣</font>\n";

        var marker = Assert.IsType<LapseSubtitleMetadata.SrtMarkerInfo>(LapseSubtitleMetadata.ReadSrtMarker(text));

        Assert.Equal("2026.10.03", marker.ApplicationVersion);
        Assert.Equal("2.2.4", marker.LapseVersion);
        Assert.Null(marker.SettingsProfile);
        Assert.Null(marker.Reference);
        Assert.True(LapseSubtitleMetadata.HasSrtMarker(text));
    }

    [Fact]
    public void TimingValidatorAcceptsOnlyTimestampChanges()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "a.srt");
        var shifted = Path.Combine(_root, "b.srt");
        var changed = Path.Combine(_root, "c.srt");
        File.WriteAllText(original, "1\n00:00:01,000 --> 00:00:02,000\nText\n");
        File.WriteAllText(shifted, "1\n00:00:03,000 --> 00:00:04,000\nText\n");
        File.WriteAllText(changed, "1\n00:00:03,000 --> 00:00:04,000\nChanged\n");

        LapseSubtitleMetadata.ValidateTimingOnlyChange(original, shifted);
        Assert.Throws<InvalidDataException>(() => LapseSubtitleMetadata.ValidateTimingOnlyChange(original, changed));
    }

    [Fact]
    public void MaximumTimingAdjustmentUsesLargestCueMovement()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "maximum-original.srt");
        var shifted = Path.Combine(_root, "maximum-shifted.srt");
        File.WriteAllText(original,
            "1\n00:00:01,000 --> 00:00:02,000\nFirst\n\n"
            + "2\n00:00:05,000 --> 00:00:06,000\nSecond\n");
        File.WriteAllText(shifted,
            "1\n00:00:01,500 --> 00:00:02,500\nFirst\n\n"
            + "2\n00:00:03,750 --> 00:00:04,750\nSecond\n");

        var maximum = LapseSubtitleMetadata.MeasureMaximumTimingAdjustmentMilliseconds(original, shifted);

        Assert.Equal(1250, maximum);
    }

    [Fact]
    public void MaximumTimingAdjustmentSupportsAssEvents()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "maximum-original.ass");
        var shifted = Path.Combine(_root, "maximum-shifted.ass");
        File.WriteAllText(original,
            "[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Text\n");
        File.WriteAllText(shifted,
            "[Events]\nDialogue: 0,0:00:02.50,0:00:03.50,Default,,0,0,0,,Text\n");

        var maximum = LapseSubtitleMetadata.MeasureMaximumTimingAdjustmentMilliseconds(original, shifted);

        Assert.Equal(1500, maximum);
    }

    [Fact]
    public void TimingValidatorPreservesBlankLinesInsideSrtCueText()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "blank-lines-original.srt");
        var shifted = Path.Combine(_root, "blank-lines-shifted.srt");
        File.WriteAllText(original, "1\n00:00:01,000 --> 00:00:02,000\n첫째\n\n둘째\n\n2\n00:00:03,000 --> 00:00:04,000\n끝\n");
        File.WriteAllText(shifted, "1\n00:00:02,000 --> 00:00:03,000\n첫째\n\n둘째\n\n2\n00:00:04,000 --> 00:00:05,000\n끝\n");

        LapseSubtitleMetadata.ValidateTimingOnlyChange(original, shifted);
    }

    [Fact]
    public void TimingValidatorAllowsUnchangedNonstandardAssEventButRejectsChanges()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "nonstandard-original.ass");
        var shifted = Path.Combine(_root, "nonstandard-shifted.ass");
        var changed = Path.Combine(_root, "nonstandard-changed.ass");
        File.WriteAllText(original,
            "[Events]\nComment: retained note\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Text\n");
        File.WriteAllText(shifted,
            "[Events]\nComment: retained note\nDialogue: 0,0:00:02.00,0:00:03.00,Default,,0,0,0,,Text\n");
        File.WriteAllText(changed,
            "[Events]\nComment: changed note\nDialogue: 0,0:00:02.00,0:00:03.00,Default,,0,0,0,,Text\n");

        LapseSubtitleMetadata.ValidateTimingOnlyChange(original, shifted);
        Assert.Equal(1000, LapseSubtitleMetadata.MeasureMaximumTimingAdjustmentMilliseconds(original, shifted));
        Assert.Throws<InvalidDataException>(() =>
            LapseSubtitleMetadata.ValidateTimingOnlyChange(original, changed));
    }

    [Fact]
    public void AssLapseMarkerIsReplacedWithoutDuplication()
    {
        const string source = "[Script Info]\n; SUBMUX_LAPSE_SYNC=old\n; SUBMUX_LAPSE_VERSION=1\nScriptType: v4.00+\n[Events]\n";

        var result = SubMuxMetadata.AddOrReplaceAssLapseMarker(
            source,
            "auto/shifted",
            "solid",
            "abc",
            "2026.10.03",
            "2.2.4",
            "AUTO|AUTO|6|8",
            "embedded",
            -243,
            1,
            0.698376);

        Assert.Equal(1, result.Split("SUBMUX_LAPSE_SYNC=", StringSplitOptions.None).Length - 1);
        Assert.Contains("; SUBMUX_LAPSE_SYNC=2026.10.03", result);
        Assert.Contains("; SUBMUX_LAPSE_MODE=auto/shifted", result);
        Assert.Contains("; SUBMUX_LAPSE_REFERENCE=EMBEDDED", result);
        Assert.Contains("; SUBMUX_LAPSE_OFFSET_MS=-243", result);
        Assert.Contains("; SUBMUX_LAPSE_RATIO=1", result);
        Assert.Contains("; SUBMUX_LAPSE_CONFIDENCE=0.698376", result);
        Assert.True(SubMuxMetadata.HasAssLapseMarker(result));
    }

    [Fact]
    public void ApplyingSrtTimingsPreservesAssStylesAndDialogueText()
    {
        const string ass = "[V4+ Styles]\nStyle: Default,Custom Font,40,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\c&HFF0000&}Text";
        const string srt = "1\n00:00:03,120 --> 00:00:04,560\nText\n";

        var result = LapseSubtitleMetadata.ApplySrtTimingsToAss(ass, srt);

        Assert.Contains("Style: Default,Custom Font,40", result);
        Assert.Contains("Dialogue: 0,0:00:03.12,0:00:04.56,Default,,0,0,0,,{\\c&HFF0000&}Text", result);
    }

    [Fact]
    public async Task SynchronizerAppliesOnlySolidWrittenOutput()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "input.srt");
        var output = Path.Combine(_root, "output.srt");
        File.WriteAllText(input, "1\n00:00:01,000 --> 00:00:02,000\nText\n");
        var runner = new LapseRunner(output, "{\"mode\":\"auto/shifted\",\"reference\":\"vad\",\"offset_ms\":25,\"ratio\":1,\"confidence\":9,\"verdict\":\"solid\",\"parts\":1,\"written\":true,\"splits\":[]}");
        var provider = CreateProvider();
        var service = new BundledLapseSynchronizer(runner, provider);

        var forwardedOutput = new List<string>();
        var result = await service.SynchronizeAsync(new LapseSyncRequest(
            "video.mkv", input, output, LapseSyncMode.Auto, 6,
            new LapseReferenceSelection("audio #0", null, 0, true)),
            forwardedOutput.Add);

        Assert.True(result.Applied);
        Assert.Equal(25, result.OffsetMilliseconds);
        Assert.Contains("--strict", runner.Request!.Arguments);
        var confidenceIndex = runner.Request.Arguments.ToList().IndexOf("--confidence");
        Assert.True(confidenceIndex >= 0);
        Assert.Equal("8", runner.Request.Arguments[confidenceIndex + 1]);
        Assert.Contains("--no-embedded", runner.Request.Arguments);
        Assert.DoesNotContain("--force", runner.Request.Arguments);
        Assert.DoesNotContain("--snap", runner.Request.Arguments);
        Assert.Empty(forwardedOutput);
    }

    [Fact]
    public async Task SynchronizerHidesOnlyTheFinalJsonAndKeepsDiagnostics()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "input.srt");
        var output = Path.Combine(_root, "output.srt");
        File.WriteAllText(input, "1\n00:00:01,000 --> 00:00:02,000\nText\n");
        const string json = "{\"mode\":\"auto/shifted\",\"reference\":\"embedded\",\"offset_ms\":25,\"ratio\":1,\"confidence\":9,\"verdict\":\"solid\",\"parts\":1,\"written\":true,\"splits\":[]}";
        var runner = new LapseRunner(output, "decoder warning\n" + json);
        var service = new BundledLapseSynchronizer(runner, CreateProvider());
        var forwardedOutput = new List<string>();

        var result = await service.SynchronizeAsync(new LapseSyncRequest(
            "video.mkv", input, output, LapseSyncMode.Auto, 6,
            new LapseReferenceSelection("subtitle #0", 0, 0, false)),
            forwardedOutput.Add);

        Assert.True(result.Applied);
        Assert.Equal(["decoder warning"], forwardedOutput);
    }

    [Fact]
    public async Task SynchronizerPassesExactlyOneEmbeddedSubtitleAndAudioFallbackTrack()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "input.srt");
        var output = Path.Combine(_root, "output.srt");
        File.WriteAllText(input, "1\n00:00:01,000 --> 00:00:02,000\nText\n");
        var runner = new LapseRunner(
            output,
            "{\"mode\":\"auto/shifted\",\"reference\":\"embedded\",\"offset_ms\":25,\"ratio\":1,\"confidence\":9,\"verdict\":\"solid\",\"parts\":1,\"written\":true,\"splits\":[]}");
        var service = new BundledLapseSynchronizer(runner, CreateProvider());

        await service.SynchronizeAsync(new LapseSyncRequest(
            "video.mkv", input, output, LapseSyncMode.Auto, 6,
            new LapseReferenceSelection("subtitle #2", 2, 1, false)));

        var arguments = runner.Request!.Arguments.ToList();
        Assert.Equal("2", arguments[arguments.IndexOf("--sub-track") + 1]);
        Assert.Equal("1", arguments[arguments.IndexOf("--audio-track") + 1]);
        Assert.DoesNotContain("--no-embedded", arguments);
        Assert.Equal(1, arguments.Count(argument => argument == "--sub-track"));
    }

    [Fact]
    public async Task SynchronizerPassesPenaltyOnlyInSplitMode()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "input.srt");
        var output = Path.Combine(_root, "output.srt");
        File.WriteAllText(input, "1\n00:00:01,000 --> 00:00:02,000\nText\n");
        var runner = new LapseRunner(output, "{\"mode\":\"split\",\"reference\":\"vad\",\"offset_ms\":25,\"ratio\":1,\"confidence\":9,\"verdict\":\"solid\",\"parts\":2,\"written\":true,\"splits\":[1000]}");
        var service = new BundledLapseSynchronizer(runner, CreateProvider());

        await service.SynchronizeAsync(new LapseSyncRequest(
            "video.mkv", input, output, LapseSyncMode.Split, 6,
            new LapseReferenceSelection("audio #0", null, 0, true)));

        var splitIndex = runner.Request!.Arguments.ToList().IndexOf("split");
        Assert.True(splitIndex >= 0);
        Assert.Equal("6", runner.Request.Arguments[splitIndex + 1]);
    }

    [Fact]
    public async Task SynchronizerRejectsAudioFallbackWhenEmbeddedReferenceIsRequired()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "input.srt");
        var output = Path.Combine(_root, "output.srt");
        File.WriteAllText(input, "1\n00:00:01,000 --> 00:00:02,000\nText\n");
        var runner = new LapseRunner(output, "{\"mode\":\"auto/shifted\",\"reference\":\"vad\",\"offset_ms\":25,\"ratio\":1,\"confidence\":9,\"verdict\":\"solid\",\"parts\":1,\"written\":true,\"splits\":[]}");
        var service = new BundledLapseSynchronizer(runner, CreateProvider());

        var result = await service.SynchronizeAsync(new LapseSyncRequest(
            "video.mkv", input, output, LapseSyncMode.Auto, 6,
            new LapseReferenceSelection("subtitle #0", 0, null, false, true)));

        Assert.Equal(LapseVerdict.Failed, result.Verdict);
        Assert.False(result.Applied);
        Assert.Contains("required embedded subtitle", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unsure", LapseVerdict.Unsure)]
    [InlineData("nothing", LapseVerdict.Nothing)]
    public async Task SynchronizerDoesNotApplyNonSolidVerdicts(string verdict, LapseVerdict expected)
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "input.srt");
        var output = Path.Combine(_root, "output.srt");
        File.WriteAllText(input, "1\n00:00:01,000 --> 00:00:02,000\nText\n");
        var runner = new LapseRunner(output,
            $"{{\"mode\":\"auto/shifted\",\"reference\":\"vad\",\"offset_ms\":25,\"ratio\":1,\"confidence\":1,\"verdict\":\"{verdict}\",\"parts\":1,\"written\":false,\"splits\":[]}}",
            createOutput: false);
        var service = new BundledLapseSynchronizer(runner, CreateProvider());

        var result = await service.SynchronizeAsync(new LapseSyncRequest(
            "video.mkv", input, output, LapseSyncMode.Auto, 6,
            new LapseReferenceSelection("audio #0", null, 0, true)));

        Assert.Equal(expected, result.Verdict);
        Assert.False(result.Applied);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ExternalReplacementCreatesUniqueBackupsAndIndex()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "E01.srt");
        var synchronized = Path.Combine(_root, "synced.srt");
        File.WriteAllText(original, "1\n00:00:01,000 --> 00:00:02,000\nOld\n");
        File.WriteAllText(synchronized, "1\n00:00:02,000 --> 00:00:03,000\nOld\n");
        var result = new LapseSyncResult(LapseVerdict.Solid, "nosplit", "vad", 1000, 1, 10, 1, [], synchronized, null);

        await new ExternalSubtitleReplacement(original, synchronized, result, 4_000_000_000, true).CommitAsync();
        File.WriteAllText(original, "1\n00:00:03,000 --> 00:00:04,000\nOld\n");
        await new ExternalSubtitleReplacement(original, synchronized, result, 4_000_000_000, true).CommitAsync();

        var backup = Path.Combine(_root, ".submux-backup", "external-subtitles");
        Assert.True(File.Exists(Path.Combine(backup, "E01.srt")));
        Assert.True(File.Exists(Path.Combine(backup, "E01 (1).srt")));
        Assert.True(File.Exists(Path.Combine(backup, ".index", "E01.srt.json")));
        Assert.True(File.Exists(Path.Combine(backup, ".index", "E01 (1).srt.json")));
        var marked = File.ReadAllText(original);
        Assert.True(LapseSubtitleMetadata.HasSrtMarker(marked));
        Assert.Equal("AUDIO", LapseSubtitleMetadata.ReadSrtMarker(marked)?.Reference);
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(backup, ".index", "E01 (1).srt.json")));
        Assert.Equal("AUDIO", index.RootElement.GetProperty("Reference").GetString());
        Assert.Equal(10, index.RootElement.GetProperty("Confidence").GetDouble());
    }

    [Fact]
    public async Task SmiReplacementMovesOriginalToBackupAndCreatesSrt()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "E02.smi");
        var synchronized = Path.Combine(_root, "synced.srt");
        File.WriteAllText(original, "<SAMI><BODY><SYNC Start=1000><P>Old</BODY></SAMI>");
        File.WriteAllText(synchronized, "1\n00:00:02,000 --> 00:00:03,000\nOld\n");
        var result = new LapseSyncResult(LapseVerdict.Solid, "nosplit", "vad", 1000, 1, 10, 1, [], synchronized, null);

        var newPath = await new ExternalSubtitleReplacement(
            original, synchronized, result, 4_000_000_000, true).CommitAsync();

        Assert.Equal(Path.Combine(_root, "E02.srt"), newPath);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(newPath));
        Assert.True(File.Exists(Path.Combine(_root, ".submux-backup", "external-subtitles", "E02.smi")));
    }

    [Fact]
    public void PresetAppliesProcessingSettingsButNotGlobalPreferences()
    {
        var source = new AppSettings
        {
            Language = AppLanguage.English,
            ConcurrentJobCount = 8,
            OutputPrefix = "Anime_",
            EnableLapseSync = true,
            LapseMode = LapseSyncMode.Split,
            LapseConfidenceThreshold = 5,
            WarnOnLargeLapseCorrection = false,
            LapseLargeCorrectionWarningSeconds = 2.5
        };
        var preset = ProcessingPresetSettings.Capture(source);
        var target = new AppSettings { Language = AppLanguage.Korean, ConcurrentJobCount = 2 };

        preset.ApplyTo(target);

        Assert.Equal("Anime_", target.OutputPrefix);
        Assert.True(target.EnableLapseSync);
        Assert.Equal(LapseSyncMode.Split, target.LapseMode);
        Assert.Equal(5, target.LapseConfidenceThreshold);
        Assert.False(target.WarnOnLargeLapseCorrection);
        Assert.Equal(2.5, target.LapseLargeCorrectionWarningSeconds);
        Assert.Equal(AppLanguage.Korean, target.Language);
        Assert.Equal(2, target.ConcurrentJobCount);
    }

    [Fact]
    public void LapseSettingsSurviveSettingsSnapshot()
    {
        var source = new AppSettings
        {
            EnableLapseSync = true,
            LapseMode = LapseSyncMode.Ols,
            LapseReference = LapseReferenceMode.AudioOnly,
            LapseSplitPenalty = 9,
            LapseConfidenceThreshold = 5,
            WarnOnLargeLapseCorrection = false,
            LapseLargeCorrectionWarningSeconds = 2.5,
            MaintenanceUpdateLapseSync = true,
            MaintenanceForceLapseResync = true,
            SelectedPresetId = "preset"
        };

        var copy = source.Copy();

        Assert.True(copy.EnableLapseSync);
        Assert.Equal(LapseSyncMode.Ols, copy.LapseMode);
        Assert.Equal(LapseReferenceMode.AudioOnly, copy.LapseReference);
        Assert.Equal(9, copy.LapseSplitPenalty);
        Assert.Equal(5, copy.LapseConfidenceThreshold);
        Assert.False(copy.WarnOnLargeLapseCorrection);
        Assert.Equal(2.5, copy.LapseLargeCorrectionWarningSeconds);
        Assert.True(copy.MaintenanceUpdateLapseSync);
        Assert.True(copy.MaintenanceForceLapseResync);
        Assert.Equal("preset", copy.SelectedPresetId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void LapseConfidenceAcceptsSupportedBoundaries(int value)
    {
        new AppSettings { LapseConfidenceThreshold = value }.Validate();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void LapseConfidenceRejectsValuesOutsideSupportedRange(int value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AppSettings { LapseConfidenceThreshold = value }.Validate());
    }

    [Fact]
    public void LargeCorrectionWarningDefaultsToOneSecond()
    {
        var settings = new AppSettings();

        Assert.True(settings.WarnOnLargeLapseCorrection);
        Assert.Equal(1, settings.LapseLargeCorrectionWarningSeconds);
        settings.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.09)]
    [InlineData(3600.1)]
    public void LargeCorrectionWarningRejectsInvalidEnabledThreshold(double value)
    {
        Assert.Throws<InvalidOperationException>(() => new AppSettings
        {
            WarnOnLargeLapseCorrection = true,
            LapseLargeCorrectionWarningSeconds = value
        }.Validate());
    }

    [Fact]
    public void DisabledLargeCorrectionWarningAllowsStoredThresholdOutsideActiveRange()
    {
        new AppSettings
        {
            WarnOnLargeLapseCorrection = false,
            LapseLargeCorrectionWarningSeconds = 0
        }.Validate();
    }

    [Fact]
    public void PresetStoreCreatesDefaultFromExistingSettingsAndPersistsOperations()
    {
        Directory.CreateDirectory(_root);
        var directory = Path.Combine(_root, "presets");
        var settings = new AppSettings { OutputPrefix = "Mine_", Language = AppLanguage.Korean };

        var store = ProcessingPresetStore.LoadOrCreate(settings, directory);
        var migrated = Assert.Single(store.Items);
        Assert.Equal("기본", migrated.Name);
        Assert.Equal("Mine_", migrated.Settings.OutputPrefix);
        Assert.NotNull(settings.SelectedPresetId);
        Assert.True(File.Exists(Path.Combine(directory, "기본.json")));

        var added = store.Add("애니 기본", new AppSettings { EnableLapseSync = true });
        store.Rename(added.Id, "애니");
        var copy = store.Duplicate(added.Id, "애니 복사본");
        store.Delete(copy.Id);
        Assert.True(File.Exists(Path.Combine(directory, "애니.json")));
        Assert.False(File.Exists(Path.Combine(directory, "애니 기본.json")));
        Assert.False(File.Exists(Path.Combine(directory, "애니 복사본.json")));
        store.Rename(migrated.Id, "내 기본");
        Assert.True(File.Exists(Path.Combine(directory, "내 기본.json")));
        store.Delete(migrated.Id);
        Assert.Throws<InvalidOperationException>(() => store.Delete(added.Id));

        var reloaded = ProcessingPresetStore.LoadOrCreate(settings, directory);
        Assert.Single(reloaded.Items);
        Assert.Contains(reloaded.Items, item => item.Name == "애니" && item.Settings.EnableLapseSync);
        Assert.Single(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public void PresetStoreSkipsBrokenFilesAndCreatesAUsableDefault()
    {
        var directory = Path.Combine(_root, "presets");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{not-json");
        var settings = new AppSettings { OutputPrefix = "Existing_" };

        var store = ProcessingPresetStore.LoadOrCreate(settings, directory);

        var preset = Assert.Single(store.Items);
        Assert.Equal("Existing_", preset.Settings.OutputPrefix);
        Assert.Single(store.LoadWarnings);
        Assert.True(File.Exists(Path.Combine(directory, "기본.json")));
        Assert.Throws<ArgumentException>(() => store.Add("invalid:name", settings));
    }

    [Fact]
    public void PresetStoreCreatesUniquelyNamedFactoryDefaultsWithoutCopyingCurrentSettings()
    {
        var directory = Path.Combine(_root, "presets");
        var current = new AppSettings { OutputPrefix = "Custom_", EnableLapseSync = true };
        var store = ProcessingPresetStore.LoadOrCreate(current, directory);

        var first = store.AddDefaults("기본");
        var second = store.AddDefaults("기본");

        Assert.Equal("기본 (1)", first.Name);
        Assert.Equal("기본 (2)", second.Name);
        Assert.Equal(OutputFileNaming.DefaultPrefix, first.Settings.OutputPrefix);
        Assert.False(first.Settings.EnableLapseSync);
        Assert.True(first.Settings.MaintenanceUpdateLapseSync);
        Assert.True(File.Exists(Path.Combine(directory, "기본 (1).json")));
        Assert.True(File.Exists(Path.Combine(directory, "기본 (2).json")));
    }

    private BundledLapseProvider CreateProvider()
    {
        var bytes = new byte[] { 1, 2, 3 };
        return new BundledLapseProvider(Path.Combine(_root, "lapse"), _ => new MemoryStream(bytes));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class LapseRunner(string outputPath, string json, bool createOutput = true) : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            foreach (var line in json.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                onOutput?.Invoke(line);
            }
            if (createOutput)
            {
                File.WriteAllText(outputPath, "1\n00:00:02,000 --> 00:00:03,000\nText\n");
            }
            return Task.FromResult(new ProcessResult(0, json, string.Empty));
        }
    }
}
