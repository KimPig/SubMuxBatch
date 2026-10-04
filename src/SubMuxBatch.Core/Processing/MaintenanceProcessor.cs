using System.Text;
using System.Globalization;
using System.Xml.Linq;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Fonts;
using SubMuxBatch.Core.Localization;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Processing;

internal sealed record MaintenanceApplicationPolicy(
    bool UpdateSubtitles,
    bool AttachFonts,
    bool RemoveFonts,
    bool ApplyAudio,
    bool RefreshTags,
    bool ApplyLapse,
    bool RemoveOtherSubtitles,
    bool RemoveChapters,
    bool CleanMetadata);

public sealed class MaintenanceProcessor(
    IProcessRunner processRunner,
    IInstalledFontResolver? fontResolver = null,
    IAudioTranscoder? audioTranscoder = null,
    ISubtitleConverter? subtitleConverter = null,
    ILapseSynchronizer? lapseSynchronizer = null)
{
    private readonly IInstalledFontResolver _fontResolver = fontResolver ?? InstalledFontResolver.System;
    private readonly IAudioTranscoder _audioTranscoder = audioTranscoder ?? new BundledFfmpegAudioTranscoder(processRunner);
    private readonly ISubtitleConverter _subtitleConverter = subtitleConverter ?? new LibSeSubtitleConverter();
    private readonly ILapseSynchronizer _lapseSynchronizer = lapseSynchronizer ?? new BundledLapseSynchronizer(processRunner);

    public async Task<JobResult> ProcessAsync(
        string sourcePath,
        AppSettings settings,
        DependencyReport dependencies,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var lapseAppliedSummaries = new List<string>();
        settings.Validate();
        var policy = CreateApplicationPolicy(settings);
        var updateLapseFromCurrentPreset = policy.ApplyLapse;
        var lapseProfile = LapseSubtitleMetadata.CreateSettingsProfile(settings);
        if (!string.Equals(Path.GetExtension(sourcePath), ".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return new JobResult(JobState.Skipped, null, warnings, CoreText.Get("Maintenance_MkvOnly"));
        }
        if (!dependencies.IsReady || dependencies.MkvMerge.Path is null)
        {
            return new JobResult(JobState.Failed, null, warnings, CoreText.Get("Maintenance_MissingTools"));
        }

        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        var workspace = CreateWorkspace(sourceDirectory);
        var preserveWorkspaceForRecovery = false;
        try
        {
            void Report(JobState state, int percent, string message) =>
                progress?.Report(new JobProgress(state, percent, message));

            void ReportLapseStart(string target, int percent) =>
                Report(
                    JobState.UpdatingAssStyle,
                    percent,
                    CoreText.Get(
                        "Maintenance_LapseStart",
                        target,
                        settings.LapseMode.ToString().ToLowerInvariant(),
                        settings.LapseConfidenceThreshold));

            void ReportLapseApplied(string target, LapseSyncResult result, int percent)
            {
                Report(JobState.UpdatingAssStyle, percent, DescribeLapseApplied(target, result));
                lapseAppliedSummaries.Add($"{target} {result.OffsetMilliseconds ?? 0:+#;-#;0}ms");
                if (!settings.WarnOnLargeLapseCorrection
                    || result.MaximumAdjustmentMilliseconds is not { } maximumAdjustment
                    || maximumAdjustment < settings.LapseLargeCorrectionWarningSeconds * 1000)
                {
                    return;
                }

                var warning = CoreText.Get(
                    "Lapse_LargeCorrectionWarning",
                    target,
                    FormatLapseSeconds(maximumAdjustment),
                    FormatLapseSeconds(settings.LapseLargeCorrectionWarningSeconds * 1000));
                warnings.Add(warning);
                Report(JobState.UpdatingAssStyle, percent, warning);
            }

            void ReportLapseNotApplied(
                string target,
                LapseSyncResult result,
                int percent,
                bool addSummaryWarning = true)
            {
                Report(JobState.UpdatingAssStyle, percent, DescribeLapseWarning(target, result));
                if (!addSummaryWarning) return;
                var summary = CoreText.Get("Maintenance_LapseSummaryKept");
                if (!warnings.Contains(summary, StringComparer.Ordinal)) warnings.Add(summary);
            }

            var mkvMerge = new MkvMergeClient(dependencies.MkvMerge.Path, processRunner);
            Report(JobState.Verifying, 4, CoreText.Get("Maintenance_Analyze"));
            var sourceIdentification = await mkvMerge.IdentifyAsync(sourcePath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var sourceInspection = sourceIdentification.Inspection;
            var mkvExtractPath = ResolveMkvExtractPath(dependencies.MkvMerge.Path);
            var tagsXml = await ExtractTagsAsync(mkvExtractPath, sourcePath, cancellationToken).ConfigureAwait(false);
            var tags = ParseTags(tagsXml);
            if (!tags.ProcessedBySubMux)
            {
                return new JobResult(JobState.Skipped, null, warnings, CoreText.Get("Maintenance_NotSubMux"));
            }
            Report(
                JobState.Verifying,
                6,
                CoreText.Get("Maintenance_TagDetected", tags.Version ?? "—"));

            string? replacementAssPath = null;
            MkvTrackInfo? replacementAssTrack = null;
            string? replacementSrtPath = null;
            MkvTrackInfo? replacementSrtTrack = null;
            string? assPathForFonts = null;
            MkvTrackInfo? managedAssTrack = null;
            MkvTrackInfo? managedSrtTrack = null;
            string? managedSrtExtractedPath = null;
            string? sourceKind = null;
            var managedSubtitleIdentityIsCertain = false;
            var managedAssWasRegenerated = false;
            var assTracks = sourceInspection.Tracks.Where(IsAssTrack).ToArray();
            var candidates = new List<AssCandidate>();
            var extractedSubtitlePaths = new Dictionary<int, string>();
            var ambiguousAssWarningPending = false;
            if ((policy.UpdateSubtitles || policy.RefreshTags || updateLapseFromCurrentPreset)
                && assTracks.Length > 0)
            {
                Report(JobState.UpdatingAssStyle, 12, CoreText.Get("Maintenance_CheckAssStyle"));
                var tracksToExtract = assTracks
                    .Concat(policy.UpdateSubtitles || updateLapseFromCurrentPreset
                        ? sourceInspection.Tracks.Where(IsSrtTrack)
                        : [])
                    .Where(static track => track.Id is not null)
                    .GroupBy(static track => track.Id!.Value)
                    .Select(group => group.First())
                    .Select(track =>
                    {
                        var extension = IsAssTrack(track) ? ".ass" : ".srt";
                        var path = Path.Combine(workspace, $"subtitle-{track.Id}{extension}");
                        extractedSubtitlePaths[track.Id!.Value] = path;
                        return (track.Id.Value, path);
                    })
                    .ToArray();
                await ExtractTracksAsync(
                        mkvExtractPath,
                        sourcePath,
                        tracksToExtract,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var track in assTracks)
                {
                    if (track.Id is null) continue;
                    var extractedPath = extractedSubtitlePaths[track.Id.Value];
                    var text = await File.ReadAllTextAsync(extractedPath, cancellationToken).ConfigureAwait(false);
                    candidates.Add(new AssCandidate(
                        track,
                        extractedPath,
                        text,
                        MatchesLegacyStyle(text, settings.MaintenanceLegacyAssStyles),
                        SubMuxMetadata.ReadSubtitleSourceMarker(text),
                        SubMuxMetadata.HasSubtitleSourceMarker(text),
                        SubMuxMetadata.HasAssLapseMarker(text),
                        SubMuxMetadata.ReadAssLapseProfile(text)));
                }

                var internallyMarked = candidates.Where(static candidate => candidate.SubtitleSource is not null).ToArray();
                AssCandidate? selected;
                if (internallyMarked.Length > 0)
                {
                    selected = internallyMarked.Length == 1 ? internallyMarked[0] : null;
                    sourceKind = selected?.SubtitleSource;
                    managedSubtitleIdentityIsCertain = selected is not null;
                    if (selected is null)
                    {
                        warnings.Add(CoreText.Get("Maintenance_AmbiguousManagedSubtitle"));
                    }
                }
                else if (candidates.Any(static candidate => candidate.HasSubtitleSourceMarker))
                {
                    selected = SelectStyledTrack(candidates.Where(static candidate => candidate.HasSubtitleSourceMarker).ToArray());
                    sourceKind = SubMuxMetadata.LegacyAssOrUnknownSource;
                    warnings.Add(CoreText.Get("Maintenance_InvalidSubtitleSourceMarker"));
                }
                else
                {
                    Report(JobState.UpdatingAssStyle, 12, CoreText.Get("Maintenance_SourceTagMissing"));
                    var legacyMatches = settings.MaintenanceDetectLegacyAss
                        ? candidates.Where(static candidate => candidate.LegacyMatch).ToArray()
                        : [];
                    if (legacyMatches.Length == 1)
                    {
                        selected = legacyMatches[0];
                        sourceKind = SubMuxMetadata.LegacySrtOrSmiSource;
                        managedSubtitleIdentityIsCertain = true;
                        Report(
                            JobState.UpdatingAssStyle,
                            18,
                            CoreText.Get("Maintenance_LegacyStyleMatched", selected.Track.Id?.ToString() ?? "?"));
                    }
                    else
                    {
                        selected = SelectStyledTrack(candidates);
                        sourceKind = SubMuxMetadata.LegacyAssOrUnknownSource;
                        ambiguousAssWarningPending = true;
                    }
                }

                if (selected is not null)
                {
                    assPathForFonts = selected.Path;
                    managedAssTrack = selected.Track;
                    managedSrtTrack = SelectStandardSrtTrack(sourceInspection.Tracks, selected.Track);
                    if (managedSrtTrack?.Id is { } managedSrtId
                        && (policy.UpdateSubtitles || updateLapseFromCurrentPreset))
                    {
                        managedSrtExtractedPath = extractedSubtitlePaths.GetValueOrDefault(managedSrtId);
                    }
                    var legacyAssPairMatches = false;
                    if (string.Equals(
                            sourceKind,
                            SubMuxMetadata.LegacyAssOrUnknownSource,
                            StringComparison.OrdinalIgnoreCase)
                        && managedSrtExtractedPath is not null)
                    {
                        legacyAssPairMatches = await LibSeSubtitleComparer.AreEquivalentAsync(
                                selected.Path,
                                managedSrtExtractedPath,
                                cancellationToken)
                            .ConfigureAwait(false);
                        Report(
                            JobState.UpdatingAssStyle,
                            15,
                            CoreText.Get(legacyAssPairMatches
                                ? "Maintenance_AssSrtTextMatched"
                                : "Maintenance_AssSrtTextMismatch"));
                        if (legacyAssPairMatches)
                        {
                            managedSubtitleIdentityIsCertain = true;
                            ambiguousAssWarningPending = false;
                        }
                    }
                    if (ambiguousAssWarningPending)
                    {
                        warnings.Add(CoreText.Get("Maintenance_AmbiguousAss"));
                    }
                    var assIsCanonical = IsAssCanonicalSource(
                        sourceKind,
                        managedSrtExtractedPath is not null,
                        legacyAssPairMatches);
                    var shouldUpdateStyle = policy.UpdateSubtitles && IsGeneratedAssSource(sourceKind);
                    var regenerated = false;
                    var updated = selected.Text;
                    LapseSyncResult? lapseResult = null;
                    string? lapseSourceHash = null;
                    string? synchronizedSrt = null;
                    var mayRunLapse = ShouldRunLapse(
                        updateLapseFromCurrentPreset,
                        settings.MaintenanceForceLapseResync,
                        sourceKind,
                        selected.HasLapseMarker,
                        selected.LapseProfile,
                        lapseProfile);
                    if (mayRunLapse && IsGeneratedAssSource(sourceKind))
                    {
                        var standardSrt = managedSrtTrack;
                        if (standardSrt?.Id is null)
                        {
                            warnings.Add(CoreText.Get("Maintenance_StandardSrtMissing"));
                        }
                        else
                        {
                            var extractedSrt = managedSrtExtractedPath
                                               ?? Path.Combine(workspace, $"lapse-standard-{standardSrt.Id.Value}.srt");
                            if (managedSrtExtractedPath is null)
                            {
                                await ExtractTrackAsync(
                                        mkvExtractPath,
                                        sourcePath,
                                        standardSrt.Id.Value,
                                        extractedSrt,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            lapseSourceHash = LapseSubtitleMetadata.ComputeSha256(extractedSrt);
                            synchronizedSrt = Path.Combine(workspace, "lapse-maintained.srt");
                            ReportLapseStart("SRT→ASS", 16);
                            lapseResult = await RunLapseAsync(
                                sourcePath, extractedSrt, synchronizedSrt, sourceInspection, settings,
                                ManagedSubtitleTrackIds(managedAssTrack, managedSrtTrack), 16, progress, cancellationToken)
                                .ConfigureAwait(false);
                            if (!lapseResult.Applied)
                            {
                                ReportLapseNotApplied("SRT→ASS", lapseResult, 17);
                                synchronizedSrt = null;
                            }
                            else
                            {
                                LapseSubtitleMetadata.ValidateTimingOnlyChange(extractedSrt, synchronizedSrt);
                                lapseResult = lapseResult with
                                {
                                    MaximumAdjustmentMilliseconds =
                                        LapseSubtitleMetadata.MeasureMaximumTimingAdjustmentMilliseconds(
                                            extractedSrt,
                                            synchronizedSrt)
                                };
                                replacementSrtPath = synchronizedSrt;
                                replacementSrtTrack = standardSrt;
                                ReportLapseApplied("SRT→ASS", lapseResult, 17);
                            }
                        }
                    }
                    else if (mayRunLapse
                             && SourceRequiresPairedStandardSrt(sourceKind)
                             && managedSrtTrack?.Id is null)
                    {
                        warnings.Add(CoreText.Get("Maintenance_StandardSrtMissing"));
                    }
                    else if (mayRunLapse)
                    {
                        lapseSourceHash = LapseSubtitleMetadata.ComputeSha256(selected.Path);
                        var synchronizedAss = Path.Combine(workspace, "lapse-maintained.ass");
                        var primaryLapseTarget = assIsCanonical && managedSrtTrack?.Id is not null
                            ? "ASS→SRT"
                            : "ASS";
                        ReportLapseStart(primaryLapseTarget, 16);
                        lapseResult = await RunLapseAsync(
                            sourcePath, selected.Path, synchronizedAss, sourceInspection, settings,
                            ManagedSubtitleTrackIds(managedAssTrack, managedSrtTrack), 16, progress, cancellationToken)
                            .ConfigureAwait(false);
                        if (lapseResult.Applied)
                        {
                            LapseSubtitleMetadata.ValidateTimingOnlyChange(selected.Path, synchronizedAss);
                            lapseResult = lapseResult with
                            {
                                MaximumAdjustmentMilliseconds =
                                    LapseSubtitleMetadata.MeasureMaximumTimingAdjustmentMilliseconds(
                                        selected.Path,
                                        synchronizedAss)
                            };
                            var synchronizedAssText = await File.ReadAllTextAsync(synchronizedAss, cancellationToken).ConfigureAwait(false);
                            var standardSrt = managedSrtTrack;
                            var pairedSubtitleReady = true;
                            LapseSyncResult? independentSrtLapseResult = null;
                            if (standardSrt?.Id is not null)
                            {
                                if (assIsCanonical)
                                {
                                    replacementSrtPath = await RegenerateSrtFromAssAsync(
                                        synchronizedAss,
                                        settings,
                                        workspace,
                                        cancellationToken).ConfigureAwait(false);
                                    replacementSrtTrack = standardSrt;
                                }
                                else
                                {
                                    var extractedSrt = managedSrtExtractedPath
                                                       ?? Path.Combine(workspace, $"lapse-independent-{standardSrt.Id.Value}.srt");
                                    if (managedSrtExtractedPath is null)
                                    {
                                        await ExtractTrackAsync(
                                                mkvExtractPath,
                                                sourcePath,
                                                standardSrt.Id.Value,
                                                extractedSrt,
                                                cancellationToken)
                                            .ConfigureAwait(false);
                                    }
                                    var synchronizedIndependentSrt = Path.Combine(workspace, "lapse-independent-maintained.srt");
                                    ReportLapseStart("SRT", 16);
                                    var srtLapseResult = await RunLapseAsync(
                                        sourcePath,
                                        extractedSrt,
                                        synchronizedIndependentSrt,
                                        sourceInspection,
                                        settings,
                                        ManagedSubtitleTrackIds(managedAssTrack, managedSrtTrack),
                                        16,
                                        progress,
                                        cancellationToken).ConfigureAwait(false);
                                    if (srtLapseResult.Applied)
                                    {
                                        LapseSubtitleMetadata.ValidateTimingOnlyChange(extractedSrt, synchronizedIndependentSrt);
                                        srtLapseResult = srtLapseResult with
                                        {
                                            MaximumAdjustmentMilliseconds =
                                                LapseSubtitleMetadata.MeasureMaximumTimingAdjustmentMilliseconds(
                                                    extractedSrt,
                                                    synchronizedIndependentSrt)
                                        };
                                        independentSrtLapseResult = srtLapseResult;
                                        if (LapseResultsAreCompatible(lapseResult, srtLapseResult))
                                        {
                                            replacementSrtPath = synchronizedIndependentSrt;
                                            replacementSrtTrack = standardSrt;
                                        }
                                        else
                                        {
                                            pairedSubtitleReady = false;
                                            Report(
                                                JobState.UpdatingAssStyle,
                                                17,
                                                CoreText.Get("Maintenance_LapsePairNotUpdated"));
                                            warnings.Add(CoreText.Get("Maintenance_LapsePairNotUpdated"));
                                        }
                                    }
                                    else
                                    {
                                        pairedSubtitleReady = false;
                                        ReportLapseNotApplied("SRT", srtLapseResult, 17, addSummaryWarning: false);
                                        Report(
                                            JobState.UpdatingAssStyle,
                                            17,
                                            CoreText.Get("Maintenance_LapsePairNotUpdated"));
                                        warnings.Add(CoreText.Get("Maintenance_LapsePairNotUpdated"));
                                    }
                                }
                            }
                            if (pairedSubtitleReady)
                            {
                                updated = synchronizedAssText;
                                ReportLapseApplied(primaryLapseTarget, lapseResult, 17);
                                if (independentSrtLapseResult is not null)
                                {
                                    ReportLapseApplied("SRT", independentSrtLapseResult, 17);
                                }
                            }
                            else
                            {
                                lapseResult = null;
                                replacementSrtPath = null;
                                replacementSrtTrack = null;
                            }
                        }
                        else
                        {
                            ReportLapseNotApplied(primaryLapseTarget, lapseResult, 17);
                        }
                    }
                    else if (updateLapseFromCurrentPreset && selected.HasLapseMarker)
                    {
                        Report(
                            JobState.UpdatingAssStyle,
                            17,
                            CoreText.Get(selected.LapseProfile is null
                                ? "Maintenance_LapseLegacyMarker"
                                : "Maintenance_LapseAlreadyApplied"));
                    }
                    else if (updateLapseFromCurrentPreset && selected.SubtitleSource is null)
                    {
                        var warning = CoreText.Get("Maintenance_LapseSourceUnknown");
                        Report(JobState.UpdatingAssStyle, 17, warning);
                        warnings.Add(warning);
                    }
                    if (synchronizedSrt is not null)
                    {
                        if (shouldUpdateStyle)
                        {
                            updated = await RegenerateAssFromSrtAsync(
                                    synchronizedSrt,
                                    settings,
                                    workspace,
                                    warnings,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            regenerated = true;
                        }
                        else
                        {
                            updated = LapseSubtitleMetadata.ApplySrtTimingsToAss(
                                selected.Text,
                                await File.ReadAllTextAsync(synchronizedSrt, cancellationToken).ConfigureAwait(false));
                        }
                    }
                    if (shouldUpdateStyle && synchronizedSrt is null)
                    {
                        var standardSrt = managedSrtTrack;
                        if (standardSrt?.Id is null)
                        {
                            warnings.Add(CoreText.Get("Maintenance_StandardSrtMissing"));
                        }
                        else
                        {
                            Report(
                                JobState.UpdatingAssStyle,
                                18,
                                CoreText.Get("Maintenance_RegenerateAss", standardSrt.Id.Value));
                            if (managedSrtExtractedPath is null)
                            {
                                managedSrtExtractedPath = Path.Combine(
                                    workspace,
                                    $"standard-{standardSrt.Id.Value}.srt");
                                await ExtractTrackAsync(
                                        mkvExtractPath,
                                        sourcePath,
                                        standardSrt.Id.Value,
                                        managedSrtExtractedPath,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            try
                            {
                                updated = await RegenerateAssFromSrtAsync(
                                        managedSrtExtractedPath,
                                        settings,
                                        workspace,
                                        warnings,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (InvalidDataException exception)
                            {
                                throw new JobSkippedException(exception.Message);
                            }
                            regenerated = true;
                            Report(
                                JobState.UpdatingAssStyle,
                                20,
                                CoreText.Get("Maintenance_RegeneratedAss", selected.Track.Id?.ToString() ?? "?"));
                        }
                    }
                    if (sourceKind is not null)
                    {
                        updated = SubMuxMetadata.AddOrReplaceSubtitleSourceMarker(updated, sourceKind);
                    }
                    if (lapseResult?.Applied == true)
                    {
                        updated = SubMuxMetadata.AddOrReplaceAssLapseMarker(
                            updated,
                            lapseResult.Mode,
                            "solid",
                            lapseSourceHash ?? string.Empty,
                            profile: lapseProfile,
                            reference: lapseResult.Reference,
                            offsetMilliseconds: lapseResult.OffsetMilliseconds,
                            ratio: lapseResult.Ratio,
                            confidence: lapseResult.Confidence);
                    }
                    else if (selected.HasLapseMarker)
                    {
                        updated = SubMuxMetadata.CopyAssLapseMarkers(selected.Text, updated);
                    }

                    if (policy.UpdateSubtitles
                        && assIsCanonical
                        && replacementSrtPath is null
                        && managedSrtTrack?.Id is { } standardSrtId)
                    {
                        Report(
                            JobState.UpdatingAssStyle,
                            18,
                            CoreText.Get("Maintenance_RegenerateSrt", standardSrtId));
                        var canonicalAssPath = Path.Combine(workspace, "canonical-ass-for-srt.ass");
                        await File.WriteAllTextAsync(
                                canonicalAssPath,
                                updated,
                                new UTF8Encoding(false),
                                cancellationToken)
                            .ConfigureAwait(false);
                        replacementSrtPath = await RegenerateSrtFromAssAsync(
                                canonicalAssPath,
                                settings,
                                workspace,
                                cancellationToken)
                            .ConfigureAwait(false);
                        replacementSrtTrack = managedSrtTrack;
                        Report(
                            JobState.UpdatingAssStyle,
                            20,
                            CoreText.Get("Maintenance_RegeneratedSrt", standardSrtId));
                    }

                    if (regenerated || !string.Equals(updated, selected.Text, StringComparison.Ordinal))
                    {
                        replacementAssPath = Path.Combine(workspace, "maintained.ass");
                        await File.WriteAllTextAsync(
                                replacementAssPath,
                                updated,
                                new UTF8Encoding(false),
                                cancellationToken)
                            .ConfigureAwait(false);
                        replacementAssTrack = selected.Track;
                        assPathForFonts = replacementAssPath;
                    }
                    managedAssWasRegenerated = regenerated;

                    Report(
                        JobState.UpdatingAssStyle,
                        19,
                        CoreText.Get(
                            "Maintenance_SubtitleSourceMarked",
                            sourceKind ?? "—"));
                    if (regenerated)
                    {
                        var style = AssStyleDefinition.Parse(settings.AssStyleLine);
                        Report(
                            JobState.UpdatingAssStyle,
                            20,
                            CoreText.Get(
                                "Maintenance_StyleUpdated",
                                selected.Track.Id?.ToString() ?? "?",
                                style.FontName));
                    }
                    else if (policy.UpdateSubtitles && !shouldUpdateStyle)
                    {
                        Report(JobState.Verifying, 20, CoreText.Get("Maintenance_OriginalAssRetained"));
                    }
                }
                else if (ambiguousAssWarningPending || sourceKind is null)
                {
                    warnings.Add(CoreText.Get("Maintenance_AmbiguousAss"));
                }
            }

            if (policy.UpdateSubtitles && assTracks.Length == 0)
            {
                sourceKind = null;
            }

            IReadOnlySet<int>? retainedSubtitleTrackIds = null;
            if (policy.RemoveOtherSubtitles)
            {
                if (managedSubtitleIdentityIsCertain
                    && managedAssTrack?.Id is not null
                    && managedSrtTrack?.Id is not null
                    && assPathForFonts is not null)
                {
                    retainedSubtitleTrackIds = new HashSet<int>
                    {
                        managedAssTrack.Id.Value,
                        managedSrtTrack.Id.Value
                    };
                    replacementAssPath ??= assPathForFonts;
                    replacementAssTrack ??= managedAssTrack;
                    if (replacementSrtPath is null)
                    {
                        replacementSrtPath = managedSrtExtractedPath;
                        if (replacementSrtPath is null)
                        {
                            replacementSrtPath = Path.Combine(
                                workspace,
                                $"retained-standard-{managedSrtTrack.Id.Value}.srt");
                            await ExtractTrackAsync(
                                    mkvExtractPath,
                                    sourcePath,
                                    managedSrtTrack.Id.Value,
                                    replacementSrtPath,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        replacementSrtTrack = managedSrtTrack;
                    }
                    Report(JobState.Verifying, 22, CoreText.Get("Maintenance_SubtitleProtectionReady"));
                }
                else
                {
                    warnings.Add(CoreText.Get("Maintenance_SubtitleCleanupDeferred"));
                }
            }

            IReadOnlyList<FontAttachmentFile> fontAttachments = [];
            var removeExistingStyleFonts = false;
            if (assPathForFonts is not null
                && policy.AttachFonts
                && managedSubtitleIdentityIsCertain
                && IsGeneratedAssSource(sourceKind))
            {
                Report(JobState.Verifying, 24, CoreText.Get("Maintenance_PrepareFont"));
                var resolvedFonts = await ResolveStyleFontsAsync(assPathForFonts, settings, workspace, cancellationToken)
                    .ConfigureAwait(false);
                fontAttachments = resolvedFonts.Attachments;
                Report(
                    JobState.Verifying,
                    26,
                    ProcessingDecisionFormatter.DescribeFontAttachments(fontAttachments));
                removeExistingStyleFonts = policy.RemoveFonts
                                           && managedSubtitleIdentityIsCertain
                                           && IsGeneratedAssSource(sourceKind)
                                           && managedAssWasRegenerated
                                           && resolvedFonts.AllRequirementsResolved
                                           && (retainedSubtitleTrackIds is not null || assTracks.Length == 1);
                if (settings.RemoveExistingFontAttachments && !removeExistingStyleFonts)
                {
                    warnings.Add(CoreText.Get("Maintenance_PreserveFonts"));
                }
            }

            AudioMuxPlan? audioMuxPlan = null;
            AudioConversionPlan? conversionPlan = null;
            if (policy.ApplyAudio
                && (settings.ConvertAudioToAac || settings.FilterAudioTracksByLanguage))
            {
                conversionPlan = AudioConversionPlanner.Create(sourceInspection, settings);
                foreach (var message in ProcessingDecisionFormatter.DescribeAudioPlan(sourceInspection, conversionPlan))
                {
                    Report(JobState.Verifying, 27, message);
                }
                var generated = new List<GeneratedAudioTrack>();
                for (var index = 0; index < conversionPlan.Transcodes.Count; index++)
                {
                    var transcode = conversionPlan.Transcodes[index];
                    var audioPath = Path.Combine(workspace, $"audio-{index + 1:00}.mka");
                    var audioProgressMessage = CoreText.Get(
                        "Maintenance_ConvertAudio",
                        index + 1,
                        conversionPlan.Transcodes.Count);
                    var transcodeCount = Math.Max(1, conversionPlan.Transcodes.Count);
                    Report(JobState.ConvertingAudio, 28 + index * 24 / transcodeCount, audioProgressMessage);
                    var result = await _audioTranscoder.TranscodeAsync(
                        new AudioTranscodeRequest(sourcePath, transcode.SourceAudioIndex, audioPath,
                            transcode.OutputChannels, transcode.BitrateKbps, sourceInspection.DurationNanoseconds),
                        onProgress: percent => Report(
                            JobState.ConvertingAudio,
                            28 + (index * 100 + percent) * 24 / (transcodeCount * 100),
                            audioProgressMessage),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    warnings.AddRange(result.Warnings.Select(static warning => $"FFmpeg: {warning}"));
                    generated.Add(new GeneratedAudioTrack(audioPath, transcode.SourceTrack,
                        transcode.OutputChannels, transcode.BitrateKbps, transcode.DefaultTrack,
                        transcode.ForcedTrack, transcode.TrackName));
                }
                audioMuxPlan = new AudioMuxPlan(conversionPlan.RetainedSourceTrackIds, generated,
                    conversionPlan.SourceDefaultTrackOverrides);
            }

            string? updatedTagsPath = null;
            if (policy.RefreshTags || (policy.CleanMetadata && settings.AddSubMuxTag))
            {
                updatedTagsPath = Path.Combine(workspace, "tags.xml");
                var updatedTags = UpdateTags(
                    policy.CleanMetadata ? "<Tags />" : tagsXml,
                    settings.AddSubMuxTag);
                await File.WriteAllTextAsync(updatedTagsPath, updatedTags, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
                Report(
                    JobState.Verifying,
                    54,
                    settings.AddSubMuxTag
                        ? CoreText.Get("Maintenance_TagsRefreshed", SubMuxMetadata.GetApplicationVersion())
                        : CoreText.Get("Maintenance_TagsRemoved"));
            }

            var hasAudioChanges = audioMuxPlan?.GeneratedTracks.Count > 0
                                  || audioMuxPlan?.SourceDefaultTrackOverrides.Count > 0
                                  || (audioMuxPlan is not null
                                      && audioMuxPlan.RetainedSourceTrackIds.Count
                                      != sourceInspection.Tracks.Count(static track =>
                                          string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase)));
            var hasFontChanges = fontAttachments.Count > 0 || removeExistingStyleFonts;
            if (replacementAssPath is null && replacementSrtPath is null
                && !hasAudioChanges && !hasFontChanges && updatedTagsPath is null
                && !policy.RemoveChapters && !policy.CleanMetadata)
            {
                return new JobResult(JobState.Skipped, null, warnings, CoreText.Get("Maintenance_NoChanges"));
            }

            var preferredOutput = Path.Combine(
                sourceDirectory,
                $"{settings.MaintenanceOutputPrefix}{Path.GetFileName(sourcePath)}");
            var outputPath = settings.MaintenanceReplaceOriginal
                ? sourcePath
                : GetAvailablePath(preferredOutput);
            Report(
                JobState.Verifying,
                56,
                CoreText.Get(
                    settings.MaintenanceReplaceOriginal ? "Maintenance_OutputReplace" : "Maintenance_OutputCopy",
                    outputPath));
            var partialPath = Path.Combine(workspace, "output.partial.mkv");
            Report(JobState.Muxing, 58, CoreText.Get("Maintenance_Mux"));
            var subtitleReplacements = new List<SubtitleTrackReplacement>();
            if (replacementAssPath is not null && replacementAssTrack is not null)
                subtitleReplacements.Add(new SubtitleTrackReplacement(replacementAssPath, replacementAssTrack));
            if (replacementSrtPath is not null && replacementSrtTrack is not null)
                subtitleReplacements.Add(new SubtitleTrackReplacement(replacementSrtPath, replacementSrtTrack));
            var muxResult = await mkvMerge.MaintainAsync(
                sourcePath, partialPath, subtitleReplacements, fontAttachments,
                removeExistingStyleFonts,
                updatedTagsPath, audioMuxPlan,
                retainedSubtitleTrackIds,
                policy.RemoveChapters,
                policy.CleanMetadata,
                percent => Report(JobState.Muxing, 58 + percent * 35 / 100, CoreText.Get("Maintenance_MuxProgress", percent)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            warnings.AddRange(muxResult.Warnings.Select(static warning => $"mkvmerge: {warning}"));
            var partialLength = new FileInfo(partialPath).Length;

            Report(JobState.Verifying, 95, CoreText.Get("Maintenance_Verify"));
            var outputInspection = await mkvMerge.InspectAsync(partialPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            ValidateOutput(
                sourceInspection, outputInspection, subtitleReplacements, audioMuxPlan,
                retainedSubtitleTrackIds, policy.RemoveChapters, policy.CleanMetadata,
                removeExistingStyleFonts, fontAttachments);
            await ValidateReplacementPayloadsAsync(
                    mkvExtractPath,
                    partialPath,
                    outputInspection,
                    sourceInspection,
                    subtitleReplacements,
                    retainedSubtitleTrackIds,
                    workspace,
                    cancellationToken)
                .ConfigureAwait(false);
            if (policy.RefreshTags || policy.CleanMetadata)
            {
                var outputTags = await ExtractTagsAsync(
                        mkvExtractPath,
                        partialPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateOutputTags(outputTags, settings.AddSubMuxTag, policy.CleanMetadata);
            }
            var originalBackupPath = settings.MaintenanceReplaceOriginal
                ? Path.Combine(workspace, "source.backup.mkv")
                : null;
            try
            {
                if (settings.MaintenanceReplaceOriginal)
                {
                    File.Replace(partialPath, sourcePath, originalBackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(partialPath, outputPath);
                }

                BatchProcessor.ValidateCommittedOutputFile(outputPath, partialLength);
                Report(JobState.Verifying, 99, ProcessingDecisionFormatter.DescribeVerifiedOutput(outputInspection));
                if (lapseAppliedSummaries.Count > 0)
                {
                    Report(
                        JobState.Verifying,
                        99,
                        CoreText.Get(
                            "Maintenance_LapseSummaryApplied",
                            string.Join(" · ", lapseAppliedSummaries)));
                }
                if (originalBackupPath is not null && File.Exists(originalBackupPath))
                {
                    File.Delete(originalBackupPath);
                }
            }
            catch (Exception commitException)
            {
                if (originalBackupPath is not null && File.Exists(originalBackupPath))
                {
                    try
                    {
                        RestoreOriginal(sourcePath, originalBackupPath);
                        Report(JobState.Verifying, 99, CoreText.Get("Maintenance_SourceRestored"));
                    }
                    catch (Exception restoreException)
                    {
                        preserveWorkspaceForRecovery = true;
                        throw new InvalidOperationException(
                            CoreText.Get(
                                "Maintenance_SourceRestoreFailed",
                                originalBackupPath,
                                restoreException.Message),
                            new AggregateException(commitException, restoreException));
                    }
                }
                throw;
            }
            return new JobResult(warnings.Count == 0 ? JobState.Succeeded : JobState.SucceededWithWarnings,
                outputPath, warnings);
        }
        catch (JobSkippedException exception)
        {
            return new JobResult(JobState.Skipped, null, warnings, exception.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new JobResult(JobState.Failed, null, warnings, exception.Message);
        }
        finally
        {
            if (!preserveWorkspaceForRecovery)
            {
                TryDeleteWorkspace(workspace, sourceDirectory);
            }
        }
    }

    private async Task<(IReadOnlyList<FontAttachmentFile> Attachments, bool AllRequirementsResolved)> ResolveStyleFontsAsync(
        string assPath, AppSettings settings, string workspace, CancellationToken cancellationToken)
    {
        var style = AssStyleDefinition.Parse(settings.AssStyleLine);
        var assText = await File.ReadAllTextAsync(assPath, cancellationToken).ConfigureAwait(false);
        var requirements = AssFontNameExtractor.ExtractRequirements(assText);
        if (requirements.Count == 0)
        {
            requirements = [new AssFontRequirement(style.FontName, style.Bold ? 700 : 400, style.Italic)];
        }

        var attachments = new List<FontAttachmentFile>();
        var allResolved = true;
        foreach (var requirement in requirements)
        {
            var match = _fontResolver.Resolve(requirement);
            if (match is not null)
            {
                attachments.Add(match.File);
                continue;
            }
            if (!string.Equals(requirement.FamilyName, "SubMux Sans", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(requirement.FamilyName, style.FontName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new JobSkippedException(CoreText.Get("Maintenance_FontNotFound", requirement.FamilyName));
                }
                allResolved = false;
                continue;
            }
            var bundled = await BatchProcessor.ExtractBundledSubMuxFontAsync(workspace, cancellationToken)
                .ConfigureAwait(false);
            attachments.Add(new FontAttachmentFile(bundled, "font/otf", "SubMuxSans-Medium.otf"));
        }
        var deduplicated = await BatchProcessor.DeduplicateAndNameFontAttachmentsAsync(attachments, cancellationToken)
            .ConfigureAwait(false);
        return (deduplicated, allResolved);
    }

    private static bool IsAssTrack(MkvTrackInfo track) =>
        string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase)
        && (track.CodecId.Contains("ASS", StringComparison.OrdinalIgnoreCase)
            || track.CodecId.Contains("SSA", StringComparison.OrdinalIgnoreCase)
            || track.CodecName?.Contains("ASS", StringComparison.OrdinalIgnoreCase) == true
            || track.CodecName?.Contains("SSA", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsSrtTrack(MkvTrackInfo track) =>
        string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase)
        && (track.CodecId.Contains("UTF8", StringComparison.OrdinalIgnoreCase)
            || track.CodecId.Contains("SRT", StringComparison.OrdinalIgnoreCase)
            || track.CodecName?.Contains("SubRip", StringComparison.OrdinalIgnoreCase) == true
            || track.CodecName?.Contains("SRT", StringComparison.OrdinalIgnoreCase) == true);

    internal static MkvTrackInfo? SelectStandardSrtTrack(
        IReadOnlyList<MkvTrackInfo> tracks,
        MkvTrackInfo styledTrack)
    {
        var srtTracks = tracks.Where(IsSrtTrack).ToArray();
        var named = srtTracks.Where(static track =>
            track.TrackName?.Contains("일반 자막", StringComparison.OrdinalIgnoreCase) == true
            || track.TrackName?.Contains("standard subtitles", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (named.Length == 1) return named[0];

        var sameLanguage = named.Where(track => LanguagesMatch(track, styledTrack)).ToArray();
        return sameLanguage.Length == 1 ? sameLanguage[0] : null;
    }

    private static bool LanguagesMatch(MkvTrackInfo left, MkvTrackInfo right)
    {
        var leftLanguage = left.LanguageIetf ?? left.Language;
        var rightLanguage = right.LanguageIetf ?? right.Language;
        return !string.IsNullOrWhiteSpace(leftLanguage)
               && !string.IsNullOrWhiteSpace(rightLanguage)
               && string.Equals(leftLanguage, rightLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> RegenerateAssFromSrtAsync(
        string extractedSrt,
        AppSettings settings,
        string workspace,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var compatibleSrt = Path.Combine(workspace, "maintenance-ass-compatible.srt");
        var preparation = await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(
            extractedSrt,
            compatibleSrt,
            settings.UseCustomAssStyle
                ? AssStyleDefinition.Parse(settings.AssStyleLine).FontSize
                : 20d,
            cancellationToken).ConfigureAwait(false);
        foreach (var warning in SubtitleCompatibilityNormalizer
                     .CreateUnrecognizedFontColourWarnings(preparation))
        {
            warnings.Add(warning);
        }

        string? stylePath = null;
        if (settings.UseCustomAssStyle)
        {
            stylePath = Path.Combine(workspace, "maintenance-default-style.ass");
            await File.WriteAllTextAsync(
                stylePath,
                AssStyleTemplateWriter.CreateHeader(settings),
                new UTF8Encoding(true),
                cancellationToken).ConfigureAwait(false);
        }

        var convertedPath = Path.Combine(workspace, "maintenance-regenerated.ass");
        await _subtitleConverter.ConvertAsync(
            compatibleSrt,
            convertedPath,
            SubtitleOutputFormat.AdvancedSubStationAlpha,
            stylePath,
            settings.PlayResX,
            settings.PlayResY,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var sourceSrt = await File.ReadAllTextAsync(compatibleSrt, cancellationToken).ConfigureAwait(false);
        var convertedAss = await File.ReadAllTextAsync(convertedPath, cancellationToken).ConfigureAwait(false);
        var adjustedAss = AssInlineStylePostProcessor.Apply(convertedAss, sourceSrt);
        var optimizedAss = AssInlineTagOptimizer.OptimizeGeneratedAss(adjustedAss);
        SubtitleConversionValidator.ValidateAssOptimization(adjustedAss, optimizedAss);
        SubtitleConversionValidator.ValidateSrtToAss(sourceSrt, optimizedAss);
        return optimizedAss;
    }

    private async Task<string> RegenerateSrtFromAssAsync(
        string assPath,
        AppSettings settings,
        string workspace,
        CancellationToken cancellationToken)
    {
        var compatibleAss = Path.Combine(workspace, "maintenance-srt-compatible.ass");
        var dialogueCount = await SubtitleCompatibilityNormalizer.PrepareAssForSrtAsync(
            assPath,
            compatibleAss,
            cancellationToken).ConfigureAwait(false);
        if (dialogueCount == 0)
        {
            throw new JobSkippedException(CoreText.Get("Batch_SkipNoValidSubtitleCues"));
        }

        var convertedSrt = Path.Combine(workspace, "maintenance-regenerated.srt");
        await _subtitleConverter.ConvertAsync(
            compatibleAss,
            convertedSrt,
            SubtitleOutputFormat.SubRip,
            null,
            settings.PlayResX,
            settings.PlayResY,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!await LibSeSubtitleComparer.AreEquivalentAsync(
                compatibleAss,
                convertedSrt,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException(CoreText.Get(
                "Subtitle_SrtValidationText",
                1,
                "00:00:00.000"));
        }
        return convertedSrt;
    }

    private async Task<LapseSyncResult> RunLapseAsync(
        string sourcePath,
        string subtitlePath,
        string outputPath,
        MkvInspection sourceInspection,
        AppSettings settings,
        IReadOnlySet<int>? excludedSubtitleTrackIds,
        int progressPercent,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var reference = LapseReferenceSelector.Select(sourceInspection, settings, excludedSubtitleTrackIds);
            progress?.Report(new JobProgress(
                JobState.UpdatingAssStyle,
                progressPercent,
                CoreText.Get("Maintenance_LapseReferenceSelected", reference.Description)));
            var result = await _lapseSynchronizer.SynchronizeAsync(
                new LapseSyncRequest(
                    sourcePath, subtitlePath, outputPath,
                    settings.LapseMode, settings.LapseSplitPenalty, reference,
                    settings.LapseConfidenceThreshold),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (reference.SubtitleOrdinal is not null
                && string.Equals(result.Reference, "vad", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new JobProgress(
                    JobState.UpdatingAssStyle,
                    progressPercent,
                    CoreText.Get("Maintenance_LapseReferenceFallback", reference.Description)));
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new LapseSyncResult(
                LapseVerdict.Failed, settings.LapseMode.ToString(), "—", null, null, null, 0, [], null, exception.Message);
        }
    }

    private static string DescribeLapseApplied(string target, LapseSyncResult result) =>
        CoreText.Get(
            "Maintenance_LapseApplied",
            target,
            result.Mode,
            result.Reference,
            FormatLapseConfidence(result.Confidence),
            result.OffsetMilliseconds ?? 0);

    private static string DescribeLapseWarning(string target, LapseSyncResult result) => result.Verdict switch
    {
        LapseVerdict.Unsure => CoreText.Get(
            "Maintenance_LapseUnsure",
            target,
            result.Mode,
            result.Reference,
            FormatLapseConfidence(result.Confidence),
            result.OffsetMilliseconds ?? 0),
        LapseVerdict.Nothing => CoreText.Get(
            "Maintenance_LapseNothing",
            target,
            result.Mode,
            result.Reference,
            FormatLapseConfidence(result.Confidence)),
        _ => CoreText.Get("Maintenance_LapseFailed", target, result.Error ?? "Unknown error")
    };

    private static string FormatLapseConfidence(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";

    private static string FormatLapseSeconds(double milliseconds) =>
        (milliseconds / 1000).ToString("0.###", CultureInfo.InvariantCulture);

    private static AssCandidate? SelectStyledTrack(IReadOnlyList<AssCandidate> candidates)
    {
        if (candidates.Count == 1) return candidates[0];
        var named = candidates.Where(candidate =>
            candidate.Track.TrackName?.Contains("스타일", StringComparison.OrdinalIgnoreCase) == true
            || candidate.Track.TrackName?.Contains("styled", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        return named.Length == 1 ? named[0] : default;
    }

    internal static bool MatchesLegacyStyle(string assText, string fingerprints)
    {
        var exactFingerprints = fingerprints
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => line.StartsWith("Style:", StringComparison.Ordinal)
                                  && AssStyleDefinition.TryParse(line, out var style)
                                  && string.Equals(style!.Name, "Default", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var styles = assText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => line.StartsWith("Style:", StringComparison.Ordinal)
                                  && AssStyleDefinition.TryParse(line, out _))
            .ToArray();
        return styles.Length == 1
               && AssStyleDefinition.TryParse(styles[0], out var onlyStyle)
               && string.Equals(onlyStyle!.Name, "Default", StringComparison.Ordinal)
               && exactFingerprints.Contains(styles[0]);
    }

    internal static string ReplaceDefaultStyle(string assText, string targetStyle)
    {
        var target = AssStyleDefinition.Parse(targetStyle).ToStyleLine();
        var lines = assText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var replaced = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith("Style:", StringComparison.OrdinalIgnoreCase)) continue;
            var comma = lines[index].IndexOf(',');
            var name = comma < 0 ? string.Empty : lines[index][(lines[index].IndexOf(':') + 1)..comma].Trim();
            if (!string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)) continue;
            lines[index] = target;
            replaced = true;
            break;
        }
        if (!replaced) throw new InvalidDataException(CoreText.Get("Maintenance_DefaultStyleMissing"));
        return string.Join(Environment.NewLine, lines);
    }

    private async Task ExtractTrackAsync(string mkvExtract, string source, int id, string output,
        CancellationToken cancellationToken)
        => await ExtractTracksAsync(
                mkvExtract,
                source,
                [(id, output)],
                cancellationToken)
            .ConfigureAwait(false);

    private async Task ExtractTracksAsync(
        string mkvExtract,
        string source,
        IReadOnlyCollection<(int Id, string Output)> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0) return;
        var arguments = new List<string>(tracks.Count + 2) { source, "tracks" };
        arguments.AddRange(tracks.Select(static track => $"{track.Id}:{track.Output}"));
        var result = await processRunner.RunAsync(
            new ProcessRequest(
                mkvExtract,
                arguments,
                Path.GetDirectoryName(tracks.First().Output)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode >= 2 || tracks.Any(static track => !File.Exists(track.Output)))
            throw new InvalidOperationException(CoreText.Get("Maintenance_ExtractAssFailed", result.StandardError.Trim()));
    }

    private async Task<string> ExtractTagsAsync(string mkvExtract, string source, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(new ProcessRequest(mkvExtract, [source, "tags"], Path.GetDirectoryName(source)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ExitCode >= 2 || string.IsNullOrWhiteSpace(result.StandardOutput)
            ? "<Tags />"
            : result.StandardOutput.TrimStart('\uFEFF').Trim();
    }

    private sealed record AssCandidate(
        MkvTrackInfo Track,
        string Path,
        string Text,
        bool LegacyMatch,
        string? SubtitleSource,
        bool HasSubtitleSourceMarker,
        bool HasLapseMarker,
        string? LapseProfile);

    private sealed record ParsedTags(bool ProcessedBySubMux, string? Version);

    private static ParsedTags ParseTags(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml);
            string? Value(string name) => document.Descendants()
                .Where(static element => element.Name.LocalName == "Simple")
                .FirstOrDefault(element => string.Equals(
                    element.Elements().FirstOrDefault(static child => child.Name.LocalName == "Name")?.Value,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                ?.Elements().FirstOrDefault(static child => child.Name.LocalName == "String")?.Value;
            var version = Value(SubMuxMetadata.VersionTagName);
            var marker = Value(SubMuxMetadata.ProcessedTagName);
            var legacy = Value(SubMuxMetadata.LegacyCommentTagName);
            return new ParsedTags(
                SubMuxMetadata.IsProcessed(version, marker, legacy),
                version);
        }
        catch
        {
            return new ParsedTags(false, null);
        }
    }

    internal static string UpdateTags(string xml, bool addSubMuxTags)
    {
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch { document = new XDocument(new XElement("Tags")); }
        var root = document.Root ?? new XElement("Tags");
        if (document.Root is null) document.Add(root);
        var tag = root.Elements("Tag").FirstOrDefault(element =>
            element.Element("Targets") is { } targets && !targets.Elements().Any());
        if (tag is null)
        {
            tag = new XElement("Tag", new XElement("Targets"));
            root.Add(tag);
        }
        var names = new[] { SubMuxMetadata.VersionTagName, SubMuxMetadata.ProcessedTagName };
        foreach (var simple in tag.Elements("Simple").Where(simple =>
                     names.Contains(simple.Element("Name")?.Value, StringComparer.OrdinalIgnoreCase)
                     || (string.Equals(
                             simple.Element("Name")?.Value,
                             SubMuxMetadata.LegacyCommentTagName,
                             StringComparison.OrdinalIgnoreCase)
                         && simple.Element("String")?.Value.Contains(
                             SubMuxMetadata.ProcessedValue,
                             StringComparison.OrdinalIgnoreCase) == true)).ToArray())
            simple.Remove();
        if (addSubMuxTags)
        {
            tag.Add(CreateSimple(SubMuxMetadata.VersionTagName, SubMuxMetadata.GetApplicationVersion()));
            tag.Add(CreateSimple(SubMuxMetadata.ProcessedTagName, SubMuxMetadata.ProcessedValue));
        }
        else if (!tag.Elements("Simple").Any())
        {
            tag.Remove();
        }
        return document.ToString();
    }

    private static bool IsGeneratedAssSource(string? source) =>
        source is "SRT" or "SMI" or SubMuxMetadata.LegacySrtOrSmiSource;

    internal static bool IsAssCanonicalSource(
        string? sourceKind,
        bool pairedStandardSrtExists,
        bool legacyPairVisibleTextMatches) =>
        string.Equals(sourceKind, "ASS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceKind, SubMuxMetadata.LegacyAssOrUnknownSource, StringComparison.OrdinalIgnoreCase)
        && (!pairedStandardSrtExists || legacyPairVisibleTextMatches);

    internal static bool SourceRequiresPairedStandardSrt(string? source) =>
        source is "ASS+SRT" or "ASS+SMI";

    private static IReadOnlySet<int> ManagedSubtitleTrackIds(
        MkvTrackInfo? assTrack,
        MkvTrackInfo? srtTrack)
    {
        var ids = new HashSet<int>();
        if (assTrack?.Id is { } assId) ids.Add(assId);
        if (srtTrack?.Id is { } srtId) ids.Add(srtId);
        return ids;
    }

    internal static MaintenanceApplicationPolicy CreateApplicationPolicy(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new MaintenanceApplicationPolicy(
            settings.MaintenanceUpdateAssStyle,
            settings.MaintenanceUpdateFonts && settings.AttachAssStyleFonts,
            settings.MaintenanceUpdateFonts && settings.RemoveExistingFontAttachments,
            settings.MaintenanceApplyAudioSettings,
            settings.MaintenanceRefreshTags,
            settings.MaintenanceUpdateLapseSync && settings.EnableLapseSync,
            settings.MaintenanceUpdateAssStyle && settings.RemoveExistingSubtitles,
            settings.RemoveChapters,
            settings.CleanOutputMetadata);
    }

    internal static bool ShouldRunLapse(
        bool applyCurrentLapseSettings,
        bool force,
        string? sourceKind,
        bool hasMarker,
        string? storedProfile,
        string currentProfile)
    {
        if (!applyCurrentLapseSettings) return false;
        if (sourceKind is null && !force) return false;
        if (force || !hasMarker) return true;
        return storedProfile is not null
               && !string.Equals(storedProfile, currentProfile, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool LapseResultsAreCompatible(LapseSyncResult left, LapseSyncResult right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!left.Applied || !right.Applied) return false;
        if (left.OffsetMilliseconds != right.OffsetMilliseconds
            || left.Parts != right.Parts
            || !NullableDoubleEquals(left.Ratio, right.Ratio))
        {
            return false;
        }

        return left.Splits.SequenceEqual(right.Splits);
    }

    private static bool NullableDoubleEquals(double? left, double? right) =>
        left.HasValue == right.HasValue
        && (!left.HasValue || Math.Abs(left.Value - right!.Value) <= 0.000000001d);

    private static XElement CreateSimple(string name, string value) =>
        new("Simple", new XElement("Name", name), new XElement("String", value));

    private static string ResolveMkvExtractPath(string mkvMergePath)
    {
        var path = Path.Combine(Path.GetDirectoryName(mkvMergePath)!, OperatingSystem.IsWindows() ? "mkvextract.exe" : "mkvextract");
        if (!File.Exists(path)) throw new FileNotFoundException(CoreText.Get("Maintenance_MkvExtractMissing"), path);
        return path;
    }

    internal static void ValidateOutput(
        MkvInspection source,
        MkvInspection output,
        IReadOnlyList<SubtitleTrackReplacement> subtitleReplacements,
        AudioMuxPlan? audioMuxPlan,
        IReadOnlySet<int>? retainedSubtitleTrackIds,
        bool removeChapters,
        bool cleanMetadata,
        bool removeExistingFonts,
        IReadOnlyList<FontAttachmentFile> addedFonts)
    {
        var sourceVideos = source.Tracks.Where(static track => IsTrackType(track, "video")).ToArray();
        var outputVideos = output.Tracks.Where(static track => IsTrackType(track, "video")).ToArray();
        if (sourceVideos.Length == 0
            || sourceVideos.Length != outputVideos.Length
            || sourceVideos.Where((track, index) => !VideoTrackMatches(track, outputVideos[index], cleanMetadata)).Any())
        {
            throw new InvalidOperationException(CoreText.Get("Maintenance_VideoValidationFailed"));
        }

        var replacementIds = subtitleReplacements
            .Select(static replacement => replacement.SourceTrack.Id
                ?? throw new InvalidOperationException(CoreText.Get("Mkv_SubtitleTrackIdMissing")))
            .ToHashSet();
        var expectedPreservedSubtitles = source.Tracks
            .Where(static track => IsTrackType(track, "subtitles"))
            .Where(track => track.Id is not null && !replacementIds.Contains(track.Id.Value))
            .Where(track => retainedSubtitleTrackIds is null || retainedSubtitleTrackIds.Contains(track.Id!.Value))
            .ToArray();
        var outputSubtitles = output.Tracks.Where(static track => IsTrackType(track, "subtitles")).ToArray();
        var expectedSubtitleCount = expectedPreservedSubtitles.Length + subtitleReplacements.Count;
        if (expectedSubtitleCount != outputSubtitles.Length)
        {
            throw new InvalidOperationException(CoreText.Get("Maintenance_SubtitleCountValidationFailed"));
        }
        for (var index = 0; index < expectedPreservedSubtitles.Length; index++)
        {
            if (!SubtitleTrackMatches(expectedPreservedSubtitles[index], outputSubtitles[index]))
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_SubtitleMetadataValidationFailed"));
            }
        }
        for (var index = 0; index < subtitleReplacements.Count; index++)
        {
            var expected = subtitleReplacements[index].SourceTrack;
            var actual = outputSubtitles[expectedPreservedSubtitles.Length + index];
            if (!SubtitleTrackMatches(expected, actual))
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_SubtitleMetadataValidationFailed"));
            }
        }

        var sourceAudio = source.Tracks.Where(static track => IsTrackType(track, "audio")).ToArray();
        var retainedAudio = audioMuxPlan is null
            ? sourceAudio
            : sourceAudio.Where(track => track.Id.HasValue
                                         && audioMuxPlan.RetainedSourceTrackIds.Contains(track.Id.Value)).ToArray();
        var generatedAudio = audioMuxPlan?.GeneratedTracks ?? [];
        var outputAudio = output.Tracks.Where(static track => IsTrackType(track, "audio")).ToArray();
        if (retainedAudio.Length + generatedAudio.Count != outputAudio.Length)
        {
            throw new InvalidOperationException(CoreText.Get("Maintenance_AudioValidationFailed"));
        }
        for (var index = 0; index < retainedAudio.Length; index++)
        {
            var expectedDefault = retainedAudio[index].Id is { } id
                                  && audioMuxPlan?.SourceDefaultTrackOverrides.TryGetValue(id, out var value) == true
                ? value
                : retainedAudio[index].DefaultTrack;
            if (!RetainedAudioTrackMatches(
                    retainedAudio[index],
                    outputAudio[index],
                    expectedDefault,
                    cleanMetadata && outputAudio.Length == 1))
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_AudioMetadataValidationFailed"));
            }
        }
        for (var index = 0; index < generatedAudio.Count; index++)
        {
            if (!GeneratedAudioTrackMatches(
                    generatedAudio[index],
                    outputAudio[retainedAudio.Length + index],
                    cleanMetadata && outputAudio.Length == 1))
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_AudioMetadataValidationFailed"));
            }
        }

        if (removeChapters)
        {
            if (output.ChapterCount is > 0)
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_ChapterValidationFailed"));
            }
        }
        else if (source.ChapterCount.HasValue && output.ChapterCount.HasValue
                 && source.ChapterCount.Value != output.ChapterCount.Value)
        {
            throw new InvalidOperationException(CoreText.Get("Maintenance_ChapterPreservationValidationFailed"));
        }

        var expectedAttachments = removeExistingFonts
            ? source.Attachments.Where(static attachment => !MkvMergeClient.IsFontAttachment(attachment)).ToArray()
            : source.Attachments.ToArray();
        if (output.AttachmentCount != expectedAttachments.Length + addedFonts.Count)
        {
            throw new InvalidOperationException(CoreText.Get("Maintenance_AttachmentValidationFailed"));
        }
        var remainingAttachments = output.Attachments.ToList();
        foreach (var expected in expectedAttachments)
        {
            var match = remainingAttachments.FindIndex(actual => MkvMergeClient.AttachmentMetadataEquals(expected, actual));
            if (match < 0)
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_AttachmentMetadataValidationFailed"));
            }
            remainingAttachments.RemoveAt(match);
        }
        foreach (var expected in addedFonts)
        {
            var match = remainingAttachments.FindIndex(actual => MkvMergeClient.AddedFontAttachmentMetadataEquals(expected, actual));
            if (match < 0)
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_AttachmentMetadataValidationFailed"));
            }
            remainingAttachments.RemoveAt(match);
        }

        if (source.DurationNanoseconds is > 0 && output.DurationNanoseconds is > 0
            && Math.Abs(source.DurationNanoseconds.Value - output.DurationNanoseconds.Value) > 2_000_000_000L)
        {
            throw new InvalidOperationException(CoreText.Get("Maintenance_DurationValidationFailed"));
        }
    }

    private static bool IsTrackType(MkvTrackInfo track, string type) =>
        string.Equals(track.Type, type, StringComparison.OrdinalIgnoreCase);

    private static bool CommonTrackMetadataMatches(MkvTrackInfo expected, MkvTrackInfo actual) =>
        MkvMergeClient.CodecMetadataEquals(expected, actual)
        && MkvMergeClient.LanguageMetadataPreserved(expected, actual)
        && expected.DefaultTrack == actual.DefaultTrack
        && expected.ForcedTrack == actual.ForcedTrack
        && expected.HearingImpaired == actual.HearingImpaired
        && expected.VisualImpaired == actual.VisualImpaired
        && expected.TextDescriptions == actual.TextDescriptions
        && expected.OriginalLanguage == actual.OriginalLanguage
        && expected.Commentary == actual.Commentary;

    private static bool VideoTrackMatches(MkvTrackInfo expected, MkvTrackInfo actual, bool cleanMetadata) =>
        CommonTrackMetadataMatches(expected, actual)
        && string.Equals(expected.PixelDimensions, actual.PixelDimensions, StringComparison.OrdinalIgnoreCase)
        && (!expected.DefaultDurationNanoseconds.HasValue
            || expected.DefaultDurationNanoseconds == actual.DefaultDurationNanoseconds)
        && string.Equals(
            cleanMetadata ? null : expected.TrackName,
            actual.TrackName,
            StringComparison.Ordinal);

    private static bool SubtitleTrackMatches(MkvTrackInfo expected, MkvTrackInfo actual) =>
        CommonTrackMetadataMatches(expected, actual)
        && string.Equals(expected.TrackName, actual.TrackName, StringComparison.Ordinal);

    private static bool RetainedAudioTrackMatches(
        MkvTrackInfo expected,
        MkvTrackInfo actual,
        bool expectedDefault,
        bool clearTrackName) =>
        MkvMergeClient.CodecMetadataEquals(expected, actual)
        && MkvMergeClient.LanguageMetadataPreserved(expected, actual)
        && expectedDefault == actual.DefaultTrack
        && expected.ForcedTrack == actual.ForcedTrack
        && MkvMergeClient.AudioFlagsPreserved(expected, actual)
        && MkvMergeClient.AudioTechnicalMetadataPreserved(expected, actual)
        && string.Equals(clearTrackName ? null : expected.TrackName, actual.TrackName, StringComparison.Ordinal);

    private static bool GeneratedAudioTrackMatches(
        GeneratedAudioTrack expected,
        MkvTrackInfo actual,
        bool clearTrackName) =>
        actual.CodecId.Contains("AAC", StringComparison.OrdinalIgnoreCase)
        && MkvMergeClient.LanguageMetadataPreserved(expected.SourceTrack, actual)
        && expected.DefaultTrack == actual.DefaultTrack
        && expected.ForcedTrack == actual.ForcedTrack
        && MkvMergeClient.AudioFlagsPreserved(expected.SourceTrack, actual)
        && (!expected.OutputChannels.HasValue || expected.OutputChannels == actual.AudioChannels)
        && (!expected.SourceTrack.AudioSamplingFrequency.HasValue
            || actual.AudioSamplingFrequency.HasValue
            && Math.Abs(expected.SourceTrack.AudioSamplingFrequency.Value - actual.AudioSamplingFrequency.Value) < 0.01)
        && string.Equals(clearTrackName ? null : expected.TrackName, actual.TrackName, StringComparison.Ordinal);

    private async Task ValidateReplacementPayloadsAsync(
        string mkvExtract,
        string outputPath,
        MkvInspection output,
        MkvInspection source,
        IReadOnlyList<SubtitleTrackReplacement> replacements,
        IReadOnlySet<int>? retainedSubtitleTrackIds,
        string workspace,
        CancellationToken cancellationToken)
    {
        if (replacements.Count == 0) return;

        var replacementIds = replacements
            .Select(static replacement => replacement.SourceTrack.Id
                ?? throw new InvalidOperationException(CoreText.Get("Mkv_SubtitleTrackIdMissing")))
            .ToHashSet();
        var preservedCount = source.Tracks
            .Where(static track => IsTrackType(track, "subtitles"))
            .Count(track => track.Id is not null
                            && !replacementIds.Contains(track.Id.Value)
                            && (retainedSubtitleTrackIds is null
                                || retainedSubtitleTrackIds.Contains(track.Id.Value)));
        var outputSubtitles = output.Tracks.Where(static track => IsTrackType(track, "subtitles")).ToArray();
        var payloads = new List<(SubtitleTrackReplacement Replacement, string Extension, string Extracted)>(
            replacements.Count);
        for (var index = 0; index < replacements.Count; index++)
        {
            var outputTrack = outputSubtitles[preservedCount + index];
            if (outputTrack.Id is null)
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_SubtitlePayloadValidationFailed"));
            }
            var extension = IsAssTrack(replacements[index].SourceTrack) ? ".ass" : ".srt";
            var extracted = Path.Combine(workspace, $"verify-subtitle-{index + 1}{extension}");
            payloads.Add((replacements[index], extension, extracted));
        }

        await ExtractTracksAsync(
                mkvExtract,
                outputPath,
                payloads.Select((payload, index) =>
                        (outputSubtitles[preservedCount + index].Id!.Value, payload.Extracted))
                    .ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var payload in payloads)
        {
            if (string.Equals(payload.Extension, ".srt", StringComparison.OrdinalIgnoreCase))
            {
                if (!await LibSeSubtitleComparer.AreEquivalentAsync(
                        payload.Replacement.Path,
                        payload.Extracted,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(CoreText.Get(
                        "Maintenance_SubtitlePayloadValidationFailed"));
                }
            }
            else
            {
                var expectedText = await File.ReadAllTextAsync(payload.Replacement.Path, cancellationToken)
                    .ConfigureAwait(false);
                var actualText = await File.ReadAllTextAsync(payload.Extracted, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        NormalizeSubtitlePayload(expectedText),
                        NormalizeSubtitlePayload(actualText),
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(CoreText.Get(
                        "Maintenance_SubtitlePayloadValidationFailed"));
                }
            }
        }
    }

    private static string NormalizeSubtitlePayload(string value) =>
        value.TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd();

    private static void ValidateOutputTags(string xml, bool expectSubMuxTags, bool cleanMetadata)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
        {
            throw new InvalidOperationException(CoreText.Get("Maintenance_TagValidationFailed"), exception);
        }

        // mkvmerge may generate fresh per-track statistics tags (for example DURATION)
        // while muxing. Metadata cleanup removes the source's global metadata; those
        // generated, explicitly targeted statistics are not leftover global tags.
        var globalTags = document.Descendants()
            .Where(static element => element.Name.LocalName == "Tag")
            .Where(static tag =>
            {
                var targets = tag.Elements().FirstOrDefault(static child => child.Name.LocalName == "Targets");
                return targets is null || !targets.Elements().Any();
            });
        var entries = globalTags
            .SelectMany(static tag => tag.Descendants().Where(static element => element.Name.LocalName == "Simple"))
            .Select(element => new
            {
                Name = element.Elements().FirstOrDefault(static child => child.Name.LocalName == "Name")?.Value,
                Value = element.Elements().FirstOrDefault(static child => child.Name.LocalName == "String")?.Value
            })
            .ToArray();
        var version = entries.FirstOrDefault(entry => string.Equals(
            entry.Name,
            SubMuxMetadata.VersionTagName,
            StringComparison.OrdinalIgnoreCase))?.Value;
        var processed = entries.FirstOrDefault(entry => string.Equals(
            entry.Name,
            SubMuxMetadata.ProcessedTagName,
            StringComparison.OrdinalIgnoreCase))?.Value;
        var legacyProcessed = entries.Any(entry => string.Equals(
                entry.Name,
                SubMuxMetadata.LegacyCommentTagName,
                StringComparison.OrdinalIgnoreCase)
            && entry.Value?.Contains(SubMuxMetadata.ProcessedValue, StringComparison.OrdinalIgnoreCase) == true);
        if (expectSubMuxTags)
        {
            if (!string.Equals(version, SubMuxMetadata.GetApplicationVersion(), StringComparison.Ordinal)
                || !string.Equals(processed, SubMuxMetadata.ProcessedValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(CoreText.Get("Maintenance_TagValidationFailed"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(version) || !string.IsNullOrWhiteSpace(processed) || legacyProcessed)
        {
            throw new InvalidOperationException(CoreText.Get("Maintenance_TagValidationFailed"));
        }

        if (cleanMetadata && entries.Any(entry =>
                !string.Equals(entry.Name, SubMuxMetadata.VersionTagName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Name, SubMuxMetadata.ProcessedTagName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(CoreText.Get("Maintenance_TagValidationFailed"));
        }
    }

    private static string GetAvailablePath(string preferred)
    {
        if (!File.Exists(preferred)) return preferred;
        var directory = Path.GetDirectoryName(preferred)!;
        var stem = Path.GetFileNameWithoutExtension(preferred);
        var extension = Path.GetExtension(preferred);
        for (var index = 1; index < int.MaxValue; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException(CoreText.Get("Maintenance_OutputNameFailed"));
    }

    private static void RestoreOriginal(string sourcePath, string backupPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Replace(backupPath, sourcePath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(backupPath, sourcePath);
        }
    }

    private static void TryDeleteWorkspace(string workspace, string expectedParent)
    {
        try
        {
            var full = Path.GetFullPath(workspace);
            var parent = Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith(WorkspaceNaming.CurrentPrefix, StringComparison.Ordinal)
                && Directory.Exists(full)) Directory.Delete(full, true);
        }
        catch { }
    }

    private static string CreateWorkspace(string sourceDirectory)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            var path = Path.Combine(sourceDirectory, $"{WorkspaceNaming.MaintenancePrefix}{id}");
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException(CoreText.Get("Batch_CreateWorkspaceFailed"));
    }
}
