using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Fonts;
using SubMuxBatch.Core.Localization;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Processing;

public sealed class BatchProcessor(
    IProcessRunner processRunner,
    IInstalledFontResolver? installedFontResolver = null,
    ISubtitleConverter? subtitleConverter = null,
    IAudioTranscoder? audioTranscoder = null,
    ILapseSynchronizer? lapseSynchronizer = null)
{
    private readonly IInstalledFontResolver _installedFontResolver =
        installedFontResolver ?? InstalledFontResolver.System;
    private readonly ISubtitleConverter _subtitleConverter =
        subtitleConverter ?? new LibSeSubtitleConverter();
    private readonly IAudioTranscoder _audioTranscoder =
        audioTranscoder ?? new BundledFfmpegAudioTranscoder(processRunner);
    private readonly ILapseSynchronizer _lapseSynchronizer =
        lapseSynchronizer ?? new BundledLapseSynchronizer(processRunner);

    public async Task<JobResult> ProcessAsync(
        MediaSet media,
        ConversionPlan plan,
        AppSettings settings,
        DependencyReport dependencies,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.IsValid || media.VideoPath is null)
        {
            return new JobResult(JobState.Failed, null, plan.Warnings, plan.Error ?? CoreText.Get("Batch_InvalidPlan"));
        }

        if (!dependencies.IsReady || dependencies.MkvMerge.Path is null)
        {
            return new JobResult(JobState.Failed, null, plan.Warnings, CoreText.Get("Batch_DependenciesMissing"));
        }

        var warnings = plan.Warnings.ToList();
        var lapseAppliedSummaries = new List<string>();
        var lapseAdjustments = new List<LapseAppliedAdjustment>();
        var lapseCheckSummaries = new List<LapseCheckSummary>();
        var currentState = JobState.Ready;
        var currentPercent = 0;
        BackupArtifactTransaction? backupTransaction = null;

        void ReportBackupRollbackErrors(IReadOnlyList<string> errors)
        {
            if (errors.Count == 0)
            {
                return;
            }

            var warning = CoreText.Get(
                "Batch_BackupRollbackWarning",
                string.Join(Environment.NewLine, errors));
            warnings.Add(warning);
            progress?.Report(new JobProgress(currentState, currentPercent, warning));
        }

        void RollbackBackups() =>
            ReportBackupRollbackErrors(backupTransaction?.Rollback() ?? []);

        void AddNegativeTimestampWarnings(
            string format,
            IReadOnlyList<NegativeSubtitleTimestampAdjustment> adjustments)
        {
            foreach (var adjustment in adjustments)
            {
                warnings.Add(adjustment.Kind switch
                {
                    SubtitleTimestampAdjustmentKind.Adjusted => CoreText.Get(
                        "Batch_NegativeSubtitleTimestampAdjusted",
                        format,
                        adjustment.LineNumber,
                        adjustment.OriginalRange,
                        adjustment.AdjustedRange),
                    SubtitleTimestampAdjustmentKind.RemovedBeforeVideoStart => CoreText.Get(
                        "Batch_NegativeSubtitleTimestampRemoved",
                        format,
                        adjustment.LineNumber,
                        adjustment.OriginalRange),
                    SubtitleTimestampAdjustmentKind.RemovedInvalidRange => CoreText.Get(
                        "Batch_InvalidSubtitleTimestampRangeRemoved",
                        format,
                        adjustment.LineNumber,
                        adjustment.OriginalRange),
                    SubtitleTimestampAdjustmentKind.RemovedInvalidTimestamp => CoreText.Get(
                        "Batch_InvalidSubtitleTimestampRemoved",
                        format,
                        adjustment.LineNumber,
                        adjustment.OriginalRange),
                    _ => throw new ArgumentOutOfRangeException(nameof(adjustment.Kind))
                });
            }
        }

        void LogToolOutput(string line)
        {
            var trimmedLine = line.TrimStart();
            if (!string.IsNullOrWhiteSpace(line)
                && !trimmedLine.StartsWith("#GUI#progress", StringComparison.Ordinal)
                && !trimmedLine.StartsWith("#GUI#warning", StringComparison.Ordinal))
            {
                progress?.Report(new JobProgress(currentState, currentPercent, line));
            }
        }

        void Report(JobState state, int percent, string message)
        {
            currentState = state;
            currentPercent = Math.Clamp(percent, 0, 100);
            progress?.Report(new JobProgress(state, currentPercent, message));
        }

        void ReportLapseStart(string target, int percent) =>
            Report(
                JobState.AnalyzingLapse,
                percent,
                CoreText.Get(
                    "Batch_LapseStart",
                    target,
                    settings.LapseMode.ToString().ToLowerInvariant(),
                    settings.LapseConfidenceThreshold));

        void ReportLapseApplied(string target, LapseSyncResult result, int percent)
        {
            Report(JobState.AnalyzingLapse, percent, DescribeLapseApplied(target, result));
            lapseAppliedSummaries.Add($"{target} {result.OffsetMilliseconds ?? 0:+#;-#;0}ms");
            lapseAdjustments.Add(new LapseAppliedAdjustment(
                target,
                result.Mode,
                result.OffsetMilliseconds,
                result.MaximumAdjustmentMilliseconds));
            if (result.ShouldWarnAboutAutoStrategy(settings.LapseMode))
            {
                var strategyWarning = CoreText.Get(
                    "Lapse_AutoStrategyWarning",
                    target,
                    result.Mode);
                warnings.Add(strategyWarning);
                Report(JobState.AnalyzingLapse, percent, strategyWarning);
            }
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
            Report(JobState.AnalyzingLapse, percent, warning);
        }

        void ReportLapseNotApplied(string target, LapseSyncResult result, int percent)
        {
            Report(JobState.AnalyzingLapse, percent, DescribeLapseWarning(target, result));
            var summary = CoreText.Get("Batch_LapseSummaryKept");
            if (!warnings.Contains(summary, StringComparer.Ordinal)) warnings.Add(summary);
        }

        void ReportLapseCheckStart(string target, int percent) =>
            Report(
                JobState.AnalyzingLapse,
                percent,
                CoreText.Get(
                    "Lapse_CheckStart",
                    target,
                    settings.LapseMode.ToString().ToLowerInvariant(),
                    settings.LapseConfidenceThreshold));

        void ReportLapseCheckResult(string target, LapseSyncResult result, int percent)
        {
            if (!result.Applied)
            {
                lapseCheckSummaries.Add(new LapseCheckSummary(
                    target,
                    result.Verdict.ToString().ToLowerInvariant(),
                    result.Mode,
                    result.OffsetMilliseconds,
                    null,
                    settings.LapseValidationWarningSeconds));
                var warning = CoreText.Get(
                    "Lapse_CheckInconclusive",
                    target,
                    result.Error ?? result.Verdict.ToString().ToLowerInvariant());
                warnings.Add(warning);
                Report(JobState.AnalyzingLapse, percent, warning);
                return;
            }

            var maximumAdjustment = result.MaximumAdjustmentMilliseconds
                                    ?? Math.Abs(result.OffsetMilliseconds ?? 0);
            lapseCheckSummaries.Add(new LapseCheckSummary(
                target,
                result.Verdict.ToString().ToLowerInvariant(),
                result.Mode,
                result.OffsetMilliseconds,
                maximumAdjustment,
                settings.LapseValidationWarningSeconds));
            Report(
                JobState.AnalyzingLapse,
                percent,
                IsShiftMode(result.Mode) && result.OffsetMilliseconds is { } offsetMilliseconds
                    ? CoreText.Get(
                        "Lapse_CheckShifted",
                        target,
                        result.Mode,
                        result.Reference,
                        FormatSignedLapseSeconds(offsetMilliseconds))
                    : CoreText.Get(
                        "Lapse_CheckSolid",
                        target,
                        result.Mode,
                        result.Reference,
                        FormatLapseSeconds(maximumAdjustment)));
            if (maximumAdjustment < settings.LapseValidationWarningSeconds * 1000)
            {
                return;
            }

            var thresholdWarning = CoreText.Get(
                "Lapse_CheckThresholdWarning",
                target,
                FormatLapseSeconds(maximumAdjustment),
                FormatLapseSeconds(settings.LapseValidationWarningSeconds * 1000));
            warnings.Add(thresholdWarning);
            Report(JobState.AnalyzingLapse, percent, thresholdWarning);
        }

        async Task TryBackupAsync(
            string backupName,
            string progressMessage,
            Func<Action<string>, Action<string>, Task<IReadOnlyList<string>>> backupAction)
        {
            Report(JobState.BackingUp, 36, progressMessage);
            var checkpoint = backupTransaction?.Checkpoint ?? new BackupArtifactCheckpoint(0, 0);
            try
            {
                var transaction = backupTransaction
                                  ?? throw new InvalidOperationException("The backup transaction is not initialized.");
                var paths = await backupAction(
                        transaction.RecordCreatedFile,
                        transaction.RecordCreatedDirectory)
                    .ConfigureAwait(false);
                if (paths.Count == 0)
                {
                    Report(JobState.BackingUp, 36, CoreText.Get("Batch_BackupNoItems", backupName));
                    return;
                }

                var displayPath = paths.Count == 1
                    ? paths[0]
                    : Path.GetDirectoryName(paths[0]) ?? paths[0];
                Report(JobState.BackingUp, 36, CoreText.Get("Batch_BackupCompleted", displayPath));
            }
            catch (OperationCanceledException)
            {
                ReportBackupRollbackErrors(backupTransaction?.RollbackTo(checkpoint) ?? []);
                throw;
            }
            catch (Exception exception)
            {
                ReportBackupRollbackErrors(backupTransaction?.RollbackTo(checkpoint) ?? []);
                var warning = CoreText.Get("Batch_BackupWarning", backupName, exception.Message);
                warnings.Add(warning);
                Report(JobState.BackingUp, 36, warning);
            }
        }

        try
        {
            settings.Validate();
            ValidateInputs(media, plan);
            var subtitleSourceTag = GetSubtitleSourceTagValue(plan);
            if (plan.SrtSource == SrtSourceKind.Existing && media.SrtPath is not null)
            {
                var existingMarkerText = await File.ReadAllTextAsync(media.SrtPath, cancellationToken).ConfigureAwait(false);
                var existingMarker = LapseSubtitleMetadata.ReadSrtMarker(existingMarkerText);
                if (string.Equals(existingMarker?.SourceFormat, "SMI", StringComparison.OrdinalIgnoreCase))
                {
                    subtitleSourceTag = plan.AssSource == AssSourceKind.Existing ? "ASS+SMI" : "SMI";
                }
            }
            Report(
                JobState.AnalyzingInput,
                2,
                CoreText.Get("Batch_SubtitleDecision", plan.Description, subtitleSourceTag));

            var preferredOutputPath = Path.Combine(
                media.Key.DirectoryPath,
                OutputFileNaming.Create(media.VideoPath, settings.OutputPrefix));

            await using var workspace = JobWorkspace.Create(media.Key.DirectoryPath);
            var mkvMerge = new MkvMergeClient(dependencies.MkvMerge.Path, processRunner);
            Report(JobState.AnalyzingInput, 4, CoreText.Get("Batch_InspectSource"));
            var sourceIdentification = await mkvMerge
                .IdentifyAsync(media.VideoPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var sourceInspection = sourceIdentification.Inspection;
            var sourceManagedSubtitleTrackIds = LapseReferenceSelector.FindSubMuxManagedSubtitleTrackIds(sourceInspection);
            var externalSubtitleReplacements = new List<ExternalSubtitleReplacement>();
            LapseSyncResult? assLapseResult = null;
            string? assLapseSourceHash = null;
            LapseSyncResult? standardSrtLapseResult = null;
            string? standardSrtLapseSourceHash = null;
            LapseSyncResult? assLapseCheckResult = null;
            string? assLapseCheckSourceHash = null;
            LapseSyncResult? standardSrtLapseCheckResult = null;
            string? standardSrtLapseCheckSourceHash = null;
            string? synchronizedExistingAssPath = null;
            string? globalTagsPath = null;
            if (settings.AddSubMuxTag)
            {
                globalTagsPath = Path.Combine(workspace.Path, "submux-tags.xml");
                await File.WriteAllTextAsync(
                    globalTagsPath,
                    SubMuxMetadata.CreateGlobalTagsXml(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            string? normalizedExistingAss = null;
            if (media.AssPath is not null
                && (plan.AssSource == AssSourceKind.Existing
                    || plan.SrtSource == SrtSourceKind.ConvertFromAss))
            {
                normalizedExistingAss = Path.Combine(workspace.Path, "source-normalized.ass");
                var sourceAssAdjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeAssTimestampsAsync(
                    media.AssPath,
                    normalizedExistingAss,
                    cancellationToken).ConfigureAwait(false);
                AddNegativeTimestampWarnings("ASS", sourceAssAdjustments);

                if (settings.EnableLapseSync || settings.EnableLapseValidationCheck)
                {
                    var sourceText = await File.ReadAllTextAsync(normalizedExistingAss, cancellationToken).ConfigureAwait(false);
                    if (!SubMuxMetadata.HasAssLapseMarker(sourceText))
                    {
                        var synchronizedAss = Path.Combine(workspace.Path, "lapse-synchronized.ass");
                        assLapseSourceHash = LapseSubtitleMetadata.ComputeSha256(media.AssPath);
                        if (settings.EnableLapseSync)
                        {
                            ReportLapseStart("ASS", 7);
                        }
                        else
                        {
                            ReportLapseCheckStart("ASS", 7);
                        }
                        assLapseResult = await RunLapseAsync(
                            media.VideoPath,
                            normalizedExistingAss,
                            synchronizedAss,
                            sourceInspection,
                            settings,
                            sourceManagedSubtitleTrackIds,
                            7,
                            progress,
                            LogToolOutput,
                            cancellationToken).ConfigureAwait(false);
                        if (assLapseResult.Applied)
                        {
                            LapseSubtitleMetadata.ValidateTimingOnlyChange(normalizedExistingAss, synchronizedAss);
                            assLapseResult = assLapseResult with
                            {
                                MaximumAdjustmentMilliseconds =
                                    LapseSubtitleMetadata.MeasureMaximumTimingAdjustmentMilliseconds(
                                        normalizedExistingAss,
                                        synchronizedAss)
                            };
                            if (settings.EnableLapseSync)
                            {
                                normalizedExistingAss = synchronizedAss;
                                synchronizedExistingAssPath = media.AssPath;
                                ReportLapseApplied("ASS", assLapseResult, 7);
                            }
                            else
                            {
                                assLapseCheckResult = assLapseResult;
                                assLapseCheckSourceHash = assLapseSourceHash;
                                ReportLapseCheckResult("ASS", assLapseCheckResult, 7);
                                assLapseResult = null;
                            }
                        }
                        else
                        {
                            if (settings.EnableLapseSync)
                            {
                                ReportLapseNotApplied("ASS", assLapseResult, 7);
                            }
                            else
                            {
                                assLapseCheckResult = assLapseResult;
                                assLapseCheckSourceHash = assLapseSourceHash;
                                ReportLapseCheckResult("ASS", assLapseCheckResult, 7);
                                assLapseResult = null;
                            }
                        }
                    }
                    else
                    {
                        Report(JobState.AnalyzingLapse, 7, CoreText.Get("Batch_LapseAlreadyApplied"));
                    }
                }
            }

            string finalSrt;
            switch (plan.SrtSource)
            {
                case SrtSourceKind.Existing:
                    finalSrt = media.SrtPath!;
                    break;

                case SrtSourceKind.ConvertFromAss:
                    Report(JobState.ConvertingAssToSrt, 8, CoreText.Get("Batch_ConvertAssToSrt"));
                    finalSrt = Path.Combine(workspace.Path, "secondary.srt");
                    var assForSrt = Path.Combine(workspace.Path, "ass-for-srt.ass");
                    var dialogueCount = await SubtitleCompatibilityNormalizer.PrepareAssForSrtAsync(
                        normalizedExistingAss!,
                        assForSrt,
                        cancellationToken).ConfigureAwait(false);
                    if (dialogueCount == 0)
                    {
                        throw new JobSkippedException(CoreText.Get("Batch_SkipNoValidSubtitleCues"));
                    }

                    var assToSrtResult = await _subtitleConverter.ConvertAsync(
                        assForSrt,
                        finalSrt,
                        SubtitleOutputFormat.SubRip,
                        null,
                        settings.PlayResX,
                        settings.PlayResY,
                        LogToolOutput,
                        cancellationToken).ConfigureAwait(false);
                    AddSubtitleConversionWarnings(warnings, assToSrtResult);
                    break;

                case SrtSourceKind.ConvertFromSmi:
                    Report(JobState.ConvertingSmiToSrt, 8, CoreText.Get("Batch_ConvertSmiToSrt"));
                    finalSrt = Path.Combine(workspace.Path, "secondary.srt");
                    var normalizedSmi = Path.Combine(workspace.Path, "normalized.smi");
                    var smiTimestampAdjustments = await SubtitleCompatibilityNormalizer.PrepareSmiForConversionAsync(
                        media.SmiPath!,
                        normalizedSmi,
                        cancellationToken).ConfigureAwait(false);
                    AddNegativeTimestampWarnings("SMI", smiTimestampAdjustments);
                    var smiToSrtResult = await _subtitleConverter.ConvertAsync(
                        normalizedSmi,
                        finalSrt,
                        SubtitleOutputFormat.SubRip,
                        null,
                        settings.PlayResX,
                        settings.PlayResY,
                        LogToolOutput,
                        cancellationToken).ConfigureAwait(false);
                    AddSubtitleConversionWarnings(warnings, smiToSrtResult);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(plan.SrtSource));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var finalSrtTextForMarker = await File.ReadAllTextAsync(finalSrt, cancellationToken).ConfigureAwait(false);
            var existingSrtLapseMarker = LapseSubtitleMetadata.ReadSrtMarker(finalSrtTextForMarker);
            var srtAlreadyLapseMarked = LapseSubtitleMetadata.HasSrtMarker(finalSrtTextForMarker);
            if (srtAlreadyLapseMarked && LapseSubtitleMetadata.HasIncompleteSrtDetails(finalSrtTextForMarker))
            {
                var warning = CoreText.Get("Batch_LapseMarkerDetailsIncomplete");
                warnings.Add(warning);
                Report(JobState.AnalyzingLapse, 9, warning);
            }
            if (srtAlreadyLapseMarked
                && string.Equals(existingSrtLapseMarker?.SourceFormat, "SMI", StringComparison.OrdinalIgnoreCase))
            {
                subtitleSourceTag = plan.AssSource == AssSourceKind.Existing ? "ASS+SMI" : "SMI";
            }
            if (srtAlreadyLapseMarked)
            {
                var markerFreeSrt = Path.Combine(workspace.Path, "marker-free.srt");
                await File.WriteAllTextAsync(
                    markerFreeSrt,
                    LapseSubtitleMetadata.RemoveSrtMarkers(await File.ReadAllTextAsync(finalSrt, cancellationToken).ConfigureAwait(false)),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
                finalSrt = markerFreeSrt;
                Report(JobState.AnalyzingLapse, 9, CoreText.Get("Batch_LapseAlreadyApplied"));
            }

            var normalizedSrt = Path.Combine(workspace.Path, "normalized.srt");
            var timestampAdjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeSrtTimestampsAsync(
                finalSrt,
                normalizedSrt,
                cancellationToken).ConfigureAwait(false);
            AddNegativeTimestampWarnings("SRT", timestampAdjustments);

            finalSrt = normalizedSrt;
            if (new FileInfo(finalSrt).Length == 0)
            {
                throw new JobSkippedException(CoreText.Get("Batch_SkipNoValidSubtitleCues"));
            }

            if (plan.AssSource == AssSourceKind.ConvertFromSrt)
            {
                try
                {
                    await SubtitleCompatibilityNormalizer.ValidateSrtFormattingForAssAsync(
                        finalSrt,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    throw new JobSkippedException(exception.Message);
                }
            }

            if ((settings.EnableLapseSync || settings.EnableLapseValidationCheck)
                && plan.SrtSource != SrtSourceKind.ConvertFromAss
                && !srtAlreadyLapseMarked)
            {
                if (settings.EnableLapseSync
                    && plan.SrtSource == SrtSourceKind.ConvertFromSmi
                    && File.Exists(Path.ChangeExtension(media.SmiPath!, ".srt")))
                {
                    var warning = CoreText.Get("Batch_LapseSmiTargetExists", Path.ChangeExtension(media.SmiPath!, ".srt"));
                    warnings.Add(warning);
                    Report(JobState.AnalyzingLapse, 12, warning);
                }
                else
                {
                    var synchronizedSrt = Path.Combine(workspace.Path, "lapse-synchronized.srt");
                    var originalExternal = plan.SrtSource == SrtSourceKind.ConvertFromSmi ? media.SmiPath! : media.SrtPath!;
                    var srtLapseSourceHash = LapseSubtitleMetadata.ComputeSha256(originalExternal);
                    if (settings.EnableLapseSync)
                    {
                        ReportLapseStart("SRT", 12);
                    }
                    else
                    {
                        ReportLapseCheckStart("SRT", 12);
                    }
                    var srtLapseResult = await RunLapseAsync(
                        media.VideoPath,
                        finalSrt,
                        synchronizedSrt,
                        sourceInspection,
                        settings,
                        sourceManagedSubtitleTrackIds,
                        12,
                        progress,
                        LogToolOutput,
                        cancellationToken).ConfigureAwait(false);
                    if (srtLapseResult.Applied)
                    {
                        LapseSubtitleMetadata.ValidateTimingOnlyChange(finalSrt, synchronizedSrt);
                        srtLapseResult = srtLapseResult with
                        {
                            MaximumAdjustmentMilliseconds =
                                LapseSubtitleMetadata.MeasureMaximumTimingAdjustmentMilliseconds(
                                    finalSrt,
                                    synchronizedSrt)
                        };
                        if (settings.EnableLapseSync)
                        {
                            finalSrt = synchronizedSrt;
                            standardSrtLapseResult = srtLapseResult;
                            standardSrtLapseSourceHash = srtLapseSourceHash;
                            externalSubtitleReplacements.Add(new ExternalSubtitleReplacement(
                                originalExternal,
                                synchronizedSrt,
                                srtLapseResult,
                                sourceInspection.DurationNanoseconds,
                                addSrtMarker: true,
                                settingsProfile: LapseSubtitleMetadata.CreateSettingsProfile(settings)));
                            if (plan.AssSource == AssSourceKind.ConvertFromSrt)
                            {
                                assLapseResult = srtLapseResult;
                                assLapseSourceHash = srtLapseSourceHash;
                            }
                            ReportLapseApplied("SRT", srtLapseResult, 12);
                        }
                        else
                        {
                            standardSrtLapseCheckResult = srtLapseResult;
                            standardSrtLapseCheckSourceHash = srtLapseSourceHash;
                            if (plan.AssSource == AssSourceKind.ConvertFromSrt)
                            {
                                assLapseCheckResult = srtLapseResult;
                                assLapseCheckSourceHash = srtLapseSourceHash;
                            }
                            ReportLapseCheckResult("SRT", srtLapseResult, 12);
                        }
                    }
                    else
                    {
                        if (settings.EnableLapseSync)
                        {
                            ReportLapseNotApplied("SRT", srtLapseResult, 12);
                        }
                        else
                        {
                            standardSrtLapseCheckResult = srtLapseResult;
                            standardSrtLapseCheckSourceHash = srtLapseSourceHash;
                            if (plan.AssSource == AssSourceKind.ConvertFromSrt)
                            {
                                assLapseCheckResult = srtLapseResult;
                                assLapseCheckSourceHash = srtLapseSourceHash;
                            }
                            ReportLapseCheckResult("SRT", srtLapseResult, 12);
                        }
                    }
                }
            }

            string finalAss;
            string? assValidationSourceSrt = null;
            switch (plan.AssSource)
            {
                case AssSourceKind.Existing:
                    finalAss = normalizedExistingAss!;
                    break;

                case AssSourceKind.ConvertFromSrt:
                    Report(JobState.ConvertingSrtToAss, 24, CoreText.Get("Batch_ConvertSrtToAss"));
                    finalAss = Path.Combine(workspace.Path, "primary.ass");
                    var compatibleSrt = Path.Combine(workspace.Path, "ass-compatible.srt");
                    try
                    {
                        var preparation = await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(
                            finalSrt,
                            compatibleSrt,
                            settings.UseCustomAssStyle
                                ? AssStyleDefinition.Parse(settings.AssStyleLine).FontSize
                                : 20d,
                            cancellationToken).ConfigureAwait(false);
                        foreach (var warning in SubtitleCompatibilityNormalizer
                                     .CreateUnrecognizedFontColourWarnings(preparation))
                        {
                            warnings.Add(warning);
                            Report(JobState.ConvertingSrtToAss, 24, warning);
                        }
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new JobSkippedException(exception.Message);
                    }
                    assValidationSourceSrt = compatibleSrt;
                    string? stylePath = null;
                    if (settings.UseCustomAssStyle)
                    {
                        stylePath = Path.Combine(workspace.Path, "default-style.ass");
                        await File.WriteAllTextAsync(
                            stylePath,
                            AssStyleTemplateWriter.CreateHeader(settings),
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                            cancellationToken).ConfigureAwait(false);
                    }

                    var srtToAssResult = await _subtitleConverter.ConvertAsync(
                        compatibleSrt,
                        finalAss,
                        SubtitleOutputFormat.AdvancedSubStationAlpha,
                        stylePath,
                        settings.PlayResX,
                        settings.PlayResY,
                        LogToolOutput,
                        cancellationToken).ConfigureAwait(false);
                    AddSubtitleConversionWarnings(warnings, srtToAssResult);

                    // Subtitle Edit keeps most inline formatting, but it can drop ASS
                    // position/move overrides carried inside SRT. Restore those tags
                    // without changing their values.
                    var convertedAss = await File.ReadAllTextAsync(finalAss, cancellationToken)
                        .ConfigureAwait(false);
                    var sourceSrt = await File.ReadAllTextAsync(compatibleSrt, cancellationToken)
                        .ConfigureAwait(false);
                    var adjustedAss = AssInlineStylePostProcessor.Apply(
                        convertedAss,
                        sourceSrt);
                    await File.WriteAllTextAsync(
                        finalAss,
                        adjustedAss,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(plan.AssSource));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var verifiedAss = Path.Combine(workspace.Path, "verified.ass");
            var finalAssAdjustments = await SubtitleCompatibilityNormalizer.NormalizeNegativeAssTimestampsAsync(
                finalAss,
                verifiedAss,
                cancellationToken).ConfigureAwait(false);
            finalAss = verifiedAss;

            string? assValidationSourceText = null;
            IReadOnlySet<int> assPrecisionCollapsedCueNumbers = new HashSet<int>();
            if (assValidationSourceSrt is not null)
            {
                assValidationSourceText = await File.ReadAllTextAsync(
                    assValidationSourceSrt,
                    cancellationToken).ConfigureAwait(false);
                assPrecisionCollapsedCueNumbers =
                    SubtitleConversionValidator.FindAssPrecisionCollapsedSrtCues(
                        assValidationSourceText,
                        finalAssAdjustments);
                if (assPrecisionCollapsedCueNumbers.Count > 0)
                {
                    Report(
                        JobState.ConvertingSrtToAss,
                        27,
                        CoreText.Get(
                            "Batch_AssPrecisionCollapsedCuesRemoved",
                            assPrecisionCollapsedCueNumbers.Count));
                }
            }

            AddNegativeTimestampWarnings(
                "ASS",
                finalAssAdjustments
                    .Where(adjustment =>
                        adjustment.CueNumber is not { } cueNumber
                        || !assPrecisionCollapsedCueNumbers.Contains(cueNumber))
                    .ToArray());

            if (assValidationSourceSrt is not null)
            {
                try
                {
                    var generatedAss = await File.ReadAllTextAsync(finalAss, cancellationToken).ConfigureAwait(false);
                    var optimizedAss = AssInlineTagOptimizer.OptimizeGeneratedAss(generatedAss);
                    SubtitleConversionValidator.ValidateAssOptimization(generatedAss, optimizedAss);
                    SubtitleConversionValidator.ValidateSrtToAss(
                        assValidationSourceText!,
                        optimizedAss,
                        assPrecisionCollapsedCueNumbers);
                    if (!string.Equals(generatedAss, optimizedAss, StringComparison.Ordinal))
                    {
                        await File.WriteAllTextAsync(
                            finalAss,
                            optimizedAss,
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (InvalidDataException exception)
                {
                    throw new JobSkippedException(exception.Message);
                }
            }

            if (settings.AddSubMuxTag)
            {
                var markedAss = SubMuxMetadata.AddOrReplaceSubtitleSourceMarker(
                    await File.ReadAllTextAsync(finalAss, cancellationToken).ConfigureAwait(false),
                    subtitleSourceTag);
                await File.WriteAllTextAsync(
                    finalAss,
                    markedAss,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
            }

            var policyMarkedAss = SubMuxMetadata.AddOrReplaceAssLapsePolicyMarker(
                await File.ReadAllTextAsync(finalAss, cancellationToken).ConfigureAwait(false),
                settings.EnableLapseSync);
            await File.WriteAllTextAsync(
                finalAss,
                policyMarkedAss,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            if (assLapseResult?.Applied == true)
            {
                var markedAss = SubMuxMetadata.AddOrReplaceAssLapseMarker(
                    await File.ReadAllTextAsync(finalAss, cancellationToken).ConfigureAwait(false),
                    assLapseResult.Mode,
                    "solid",
                    assLapseSourceHash ?? LapseSubtitleMetadata.ComputeSha256(
                        plan.AssSource == AssSourceKind.Existing ? media.AssPath! : finalSrt),
                    profile: LapseSubtitleMetadata.CreateSettingsProfile(settings),
                    reference: assLapseResult.Reference,
                    offsetMilliseconds: assLapseResult.OffsetMilliseconds,
                    ratio: assLapseResult.Ratio,
                    confidence: assLapseResult.Confidence,
                    sourceFormat: plan.AssSource == AssSourceKind.Existing
                        ? "ASS"
                        : plan.SrtSource == SrtSourceKind.ConvertFromSmi ? "SMI" : "SRT");
                await File.WriteAllTextAsync(
                    finalAss,
                    markedAss,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
                if (plan.AssSource == AssSourceKind.Existing && synchronizedExistingAssPath is not null)
                {
                    externalSubtitleReplacements.Add(new ExternalSubtitleReplacement(
                        synchronizedExistingAssPath,
                        finalAss,
                        assLapseResult,
                        sourceInspection.DurationNanoseconds,
                        addSrtMarker: false,
                        settingsProfile: LapseSubtitleMetadata.CreateSettingsProfile(settings)));
                }
            }
            else if (srtAlreadyLapseMarked && plan.AssSource == AssSourceKind.ConvertFromSrt)
            {
                var markedAss = SubMuxMetadata.AddOrReplaceAssLapseMarker(
                    await File.ReadAllTextAsync(finalAss, cancellationToken).ConfigureAwait(false),
                    existingSrtLapseMarker?.Mode ?? "existing",
                    existingSrtLapseMarker?.Result ?? "solid",
                    existingSrtLapseMarker?.SourceSha256 ?? LapseSubtitleMetadata.ComputeSha256(media.SrtPath!),
                    applicationVersion: existingSrtLapseMarker?.ApplicationVersion,
                    lapseVersion: existingSrtLapseMarker?.LapseVersion,
                    profile: existingSrtLapseMarker?.SettingsProfile,
                    reference: existingSrtLapseMarker?.Reference,
                    offsetMilliseconds: existingSrtLapseMarker?.OffsetMilliseconds,
                    ratio: existingSrtLapseMarker?.Ratio,
                    confidence: existingSrtLapseMarker?.Confidence,
                    sourceFormat: existingSrtLapseMarker?.SourceFormat ?? "SRT");
                await File.WriteAllTextAsync(
                    finalAss,
                    markedAss,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
            }

            if (plan.AssSource == AssSourceKind.Existing
                && plan.SrtSource != SrtSourceKind.ConvertFromAss
                && (standardSrtLapseResult?.Applied == true || srtAlreadyLapseMarked))
            {
                var markedAss = await File.ReadAllTextAsync(finalAss, cancellationToken).ConfigureAwait(false);
                if (standardSrtLapseResult?.Applied == true)
                {
                    markedAss = SubMuxMetadata.AddOrReplaceAssSrtLapseMarker(
                        markedAss,
                        standardSrtLapseResult.Mode,
                        "solid",
                        standardSrtLapseSourceHash ?? string.Empty,
                        profile: LapseSubtitleMetadata.CreateSettingsProfile(settings),
                        reference: standardSrtLapseResult.Reference,
                        offsetMilliseconds: standardSrtLapseResult.OffsetMilliseconds,
                        ratio: standardSrtLapseResult.Ratio,
                        confidence: standardSrtLapseResult.Confidence,
                        sourceFormat: plan.SrtSource == SrtSourceKind.ConvertFromSmi ? "SMI" : "SRT");
                }
                else
                {
                    markedAss = SubMuxMetadata.AddOrReplaceAssSrtLapseMarker(
                        markedAss,
                        existingSrtLapseMarker?.Mode ?? "existing",
                        existingSrtLapseMarker?.Result ?? "solid",
                        existingSrtLapseMarker?.SourceSha256
                        ?? LapseSubtitleMetadata.ComputeSha256(media.SrtPath!),
                        applicationVersion: existingSrtLapseMarker?.ApplicationVersion,
                        lapseVersion: existingSrtLapseMarker?.LapseVersion,
                        profile: existingSrtLapseMarker?.SettingsProfile,
                        reference: existingSrtLapseMarker?.Reference,
                        offsetMilliseconds: existingSrtLapseMarker?.OffsetMilliseconds,
                        ratio: existingSrtLapseMarker?.Ratio,
                        confidence: existingSrtLapseMarker?.Confidence,
                        sourceFormat: existingSrtLapseMarker?.SourceFormat ?? "SRT");
                }
                await File.WriteAllTextAsync(
                    finalAss,
                    markedAss,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
            }

            if (settings.EnableLapseValidationCheck && assLapseCheckResult is not null)
            {
                var checkedAss = SubMuxMetadata.AddOrReplaceAssLapseCheckMarker(
                    await File.ReadAllTextAsync(finalAss, cancellationToken).ConfigureAwait(false),
                    assLapseCheckResult.Mode,
                    assLapseCheckResult.Verdict.ToString().ToLowerInvariant(),
                    assLapseCheckSourceHash ?? string.Empty,
                    assLapseCheckResult.MaximumAdjustmentMilliseconds,
                    profile: LapseSubtitleMetadata.CreateSettingsProfile(settings),
                    reference: assLapseCheckResult.Reference,
                    offsetMilliseconds: assLapseCheckResult.OffsetMilliseconds,
                    ratio: assLapseCheckResult.Ratio,
                    confidence: assLapseCheckResult.Confidence,
                    sourceFormat: plan.AssSource == AssSourceKind.Existing
                        ? "ASS"
                        : plan.SrtSource == SrtSourceKind.ConvertFromSmi ? "SMI" : "SRT");
                await File.WriteAllTextAsync(
                    finalAss,
                    checkedAss,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
            }

            if (settings.EnableLapseValidationCheck
                && plan.AssSource == AssSourceKind.Existing
                && plan.SrtSource != SrtSourceKind.ConvertFromAss
                && standardSrtLapseCheckResult is not null)
            {
                var checkedAss = SubMuxMetadata.AddOrReplaceAssSrtLapseCheckMarker(
                    await File.ReadAllTextAsync(finalAss, cancellationToken).ConfigureAwait(false),
                    standardSrtLapseCheckResult.Mode,
                    standardSrtLapseCheckResult.Verdict.ToString().ToLowerInvariant(),
                    standardSrtLapseCheckSourceHash ?? string.Empty,
                    standardSrtLapseCheckResult.MaximumAdjustmentMilliseconds,
                    profile: LapseSubtitleMetadata.CreateSettingsProfile(settings),
                    reference: standardSrtLapseCheckResult.Reference,
                    offsetMilliseconds: standardSrtLapseCheckResult.OffsetMilliseconds,
                    ratio: standardSrtLapseCheckResult.Ratio,
                    confidence: standardSrtLapseCheckResult.Confidence,
                    sourceFormat: plan.SrtSource == SrtSourceKind.ConvertFromSmi ? "SMI" : "SRT");
                await File.WriteAllTextAsync(
                    finalAss,
                    checkedAss,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<FontAttachmentFile> fontAttachments = [];
            if (settings.AttachAssStyleFonts)
            {
                Report(JobState.PreparingFonts, 32, CoreText.Get("Batch_FindFonts"));
                fontAttachments = await ResolveAssFontAttachmentsAsync(
                    finalAss,
                    plan,
                    settings,
                    workspace.Path,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
                Report(
                    JobState.PreparingFonts,
                    33,
                    ProcessingDecisionFormatter.DescribeFontAttachments(fontAttachments));
            }

            var audioDecisionPlan = settings.ConvertAudioToAac || settings.FilterAudioTracksByLanguage
                ? AudioConversionPlanner.Create(sourceInspection, settings)
                : null;
            var audioDecisionMessages = audioDecisionPlan is null
                ? ProcessingDecisionFormatter.DescribeUnchangedAudio(sourceInspection)
                : ProcessingDecisionFormatter.DescribeAudioPlan(sourceInspection, audioDecisionPlan);
            foreach (var message in audioDecisionMessages)
            {
                Report(JobState.PreparingJob, 35, message);
            }
            if (settings.RemoveExistingSubtitles)
            {
                Report(JobState.PreparingJob, 35, CoreText.Get("Batch_RemoveExistingSubtitlesDecision"));
            }
            if (settings.RemoveExistingFontAttachments)
            {
                Report(JobState.PreparingJob, 35, CoreText.Get("Batch_RemoveExistingFontsDecision"));
            }
            if (settings.RemoveChapters)
            {
                Report(JobState.PreparingJob, 35, CoreText.Get("Batch_RemoveChaptersDecision"));
            }
            if (settings.CleanOutputMetadata)
            {
                Report(JobState.PreparingJob, 35, CoreText.Get("Batch_CleanMetadataDecision"));
            }
            if (settings.AddSubMuxTag)
            {
                Report(
                    JobState.PreparingJob,
                    35,
                    CoreText.Get("Batch_AddTagsDecision", SubMuxMetadata.GetApplicationVersion(), subtitleSourceTag));
            }
            var needsAudioPlan = settings.ConvertAudioToAac
                                 || (settings.BackupExcludedAudioTracks && settings.FilterAudioTracksByLanguage);
            var audioConversionPlan = needsAudioPlan
                ? audioDecisionPlan ?? AudioConversionPlanner.Create(sourceInspection, settings)
                : null;
            var backupService = new MetadataBackupService(
                processRunner,
                workingDirectory: workspace.Path);
            backupTransaction = new BackupArtifactTransaction(media.VideoPath);

            if (settings.BackupOriginalMetadata)
            {
                await TryBackupAsync(
                    CoreText.Get("Backup_Metadata"),
                    CoreText.Get("Batch_BackupMetadata"),
                    async (onBackupCreated, onBackupDirectoryCreated) =>
                    [
                        await backupService.BackupMetadataAsync(
                            media.VideoPath,
                            dependencies.MkvMerge.Path,
                            sourceIdentification,
                            cancellationToken,
                            onBackupCreated,
                            onBackupDirectoryCreated).ConfigureAwait(false)
                    ]).ConfigureAwait(false);
            }

            if (settings.BackupOriginalSubtitles)
            {
                await TryBackupAsync(
                    CoreText.Get("Backup_Subtitles"),
                    CoreText.Get("Batch_BackupSubtitles"),
                    (onBackupCreated, onBackupDirectoryCreated) => backupService.BackupSubtitlesAsync(
                        media.VideoPath,
                        dependencies.MkvMerge.Path,
                        sourceIdentification,
                        cancellationToken,
                        onBackupCreated,
                        onBackupDirectoryCreated)).ConfigureAwait(false);
            }

            if (settings.BackupOriginalAttachments)
            {
                await TryBackupAsync(
                    CoreText.Get("Backup_Attachments"),
                    CoreText.Get("Batch_BackupAttachments"),
                    (onBackupCreated, onBackupDirectoryCreated) => backupService.BackupAttachmentsAsync(
                        media.VideoPath,
                        dependencies.MkvMerge.Path,
                        sourceIdentification,
                        cancellationToken,
                        onBackupCreated,
                        onBackupDirectoryCreated)).ConfigureAwait(false);
            }

            if (settings.BackupExcludedAudioTracks && audioConversionPlan is not null)
            {
                var retainedAudioIds = audioConversionPlan.RetainedSourceTrackIds;
                var excludedAudioIds = sourceInspection.Tracks
                    .Where(static track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase))
                    .Select(static track => track.Id
                        ?? throw new InvalidOperationException(CoreText.Get("Mkv_AudioTrackIdMissing")))
                    .Where(trackId => !retainedAudioIds.Contains(trackId))
                    .ToArray();
                await TryBackupAsync(
                    CoreText.Get("Backup_ExcludedAudio"),
                    CoreText.Get("Batch_BackupExcludedAudio"),
                    async (onBackupCreated, onBackupDirectoryCreated) =>
                    {
                        var path = await backupService.BackupAudioTracksAsync(
                            media.VideoPath,
                            dependencies.MkvMerge.Path,
                            sourceIdentification,
                            excludedAudioIds,
                            cancellationToken,
                            onBackupCreated,
                            onBackupDirectoryCreated).ConfigureAwait(false);
                        return path is null ? [] : [path];
                    }).ConfigureAwait(false);
            }

            AudioMuxPlan? audioMuxPlan = null;
            if (settings.ConvertAudioToAac)
            {
                audioConversionPlan ??= AudioConversionPlanner.Create(sourceInspection, settings);
                var generatedTracks = new List<GeneratedAudioTrack>();
                for (var index = 0; index < audioConversionPlan.Transcodes.Count; index++)
                {
                    var transcode = audioConversionPlan.Transcodes[index];
                    var audioPath = Path.Combine(workspace.Path, $"audio-{index + 1:00}.mka");
                    Report(
                        JobState.ConvertingAudio,
                        38 + (int)Math.Round(index / (double)Math.Max(1, audioConversionPlan.Transcodes.Count) * 16),
                        CoreText.Get("Batch_ConvertAudio", index + 1, audioConversionPlan.Transcodes.Count));
                    var transcodeResult = await _audioTranscoder.TranscodeAsync(
                        new AudioTranscodeRequest(
                            media.VideoPath,
                            transcode.SourceAudioIndex,
                            audioPath,
                            transcode.OutputChannels,
                            transcode.BitrateKbps,
                            sourceInspection.DurationNanoseconds),
                        audioPercent =>
                        {
                            var itemStart = index / (double)Math.Max(1, audioConversionPlan.Transcodes.Count);
                            var itemProgress = audioPercent / 100d / Math.Max(1, audioConversionPlan.Transcodes.Count);
                            var totalPercent = 38 + (int)Math.Round((itemStart + itemProgress) * 16);
                            Report(
                                JobState.ConvertingAudio,
                                totalPercent,
                                CoreText.Get("Batch_ConvertAudioProgress", index + 1, audioConversionPlan.Transcodes.Count, audioPercent));
                        },
                        LogToolOutput,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var warning in transcodeResult.Warnings)
                    {
                        warnings.Add($"FFmpeg: {warning}");
                    }

                    var audioInspection = await mkvMerge
                        .InspectAsync(audioPath, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    ValidateGeneratedAudio(audioInspection, transcode.OutputChannels);
                    generatedTracks.Add(new GeneratedAudioTrack(
                        audioPath,
                        transcode.SourceTrack,
                        transcode.OutputChannels,
                        transcode.BitrateKbps,
                        transcode.DefaultTrack,
                        transcode.ForcedTrack,
                        transcode.TrackName));
                }

                audioMuxPlan = new AudioMuxPlan(
                    audioConversionPlan.RetainedSourceTrackIds,
                    generatedTracks,
                    audioConversionPlan.SourceDefaultTrackOverrides);
            }

            var partialPath = Path.Combine(workspace.Path, "output.partial.mkv");
            var encodedAnyAudio = audioMuxPlan?.GeneratedTracks.Count > 0;
            var muxStartPercent = encodedAnyAudio ? 54 : 38;
            var muxRangePercent = encodedAnyAudio ? 38 : 54;
            Report(JobState.Muxing, muxStartPercent, CoreText.Get("Batch_MuxSubtitles"));
            var muxResult = await mkvMerge.MuxAsync(
                media.VideoPath,
                finalAss,
                finalSrt,
                partialPath,
                muxPercent =>
                {
                    var totalPercent = muxStartPercent + (int)Math.Round(muxPercent * (muxRangePercent / 100d));
                    Report(JobState.Muxing, totalPercent, CoreText.Get("Batch_MuxProgress", muxPercent));
                },
                LogToolOutput,
                cancellationToken,
                removeExistingSubtitles: settings.RemoveExistingSubtitles,
                removeExistingFontAttachments: settings.RemoveExistingFontAttachments,
                removeChapters: settings.RemoveChapters,
                keepOnlyAudioLanguage: !settings.ConvertAudioToAac && settings.FilterAudioTracksByLanguage
                    ? settings.SelectedAudioLanguage
                    : null,
                fontAttachments: fontAttachments,
                globalTagsPath: globalTagsPath,
                cleanOutputMetadata: settings.CleanOutputMetadata,
                audioMuxPlan: audioMuxPlan).ConfigureAwait(false);

            Report(JobState.Verifying, 94, CoreText.Get("Batch_VerifyOutput"));
            var outputInspection = await mkvMerge.InspectAsync(partialPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var validationErrors = MkvMergeClient.ValidateOutput(
                sourceInspection,
                outputInspection,
                removeExistingSubtitles: settings.RemoveExistingSubtitles,
                removeExistingFontAttachments: settings.RemoveExistingFontAttachments,
                removeChapters: settings.RemoveChapters,
                keepOnlyAudioLanguage: !settings.ConvertAudioToAac && settings.FilterAudioTracksByLanguage
                    ? settings.SelectedAudioLanguage
                    : null,
                addedFontAttachments: fontAttachments,
                cleanOutputMetadata: settings.CleanOutputMetadata,
                audioMuxPlan: audioMuxPlan);
            if (validationErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    CoreText.Get("Batch_OutputValidationFailed") + Environment.NewLine + string.Join(Environment.NewLine, validationErrors));
            }

            foreach (var warning in muxResult.Warnings)
            {
                warnings.Add($"mkvmerge: {warning}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var partialLength = new FileInfo(partialPath).Length;
            // Commit with an atomic, non-overwriting move. Selecting the candidate
            // here keeps concurrent jobs from silently replacing one another.
            var outputPath = CommitToAvailableOutput(partialPath, preferredOutputPath);

            Report(JobState.Verifying, 98, CoreText.Get("Batch_VerifyCommittedOutput"));
            ValidateCommittedOutputFile(outputPath, partialLength);
            Report(JobState.Verifying, 99, ProcessingDecisionFormatter.DescribeVerifiedOutput(outputInspection));

            foreach (var externalSubtitleReplacement in externalSubtitleReplacements)
            {
                try
                {
                    var replacedPath = await externalSubtitleReplacement.CommitAsync(cancellationToken).ConfigureAwait(false);
                    Report(JobState.Finalizing, 99, CoreText.Get("Batch_LapseExternalCommitted", replacedPath));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var warning = CoreText.Get("Batch_LapseExternalCommitFailed", exception.Message);
                    warnings.Add(warning);
                    Report(JobState.Finalizing, 99, warning);
                }
            }

            if (lapseAppliedSummaries.Count > 0)
            {
                Report(
                    JobState.Finalizing,
                    99,
                    CoreText.Get("Batch_LapseSummaryApplied", string.Join(" · ", lapseAppliedSummaries)));
            }
            foreach (var check in lapseCheckSummaries)
            {
                Report(
                    JobState.Finalizing,
                    99,
                    DescribeLapseCheckSummary(check));
            }

            Report(JobState.Finalizing, 99, CoreText.Get("Batch_CleanupWorkspace"));
            await workspace.DisposeAsync().ConfigureAwait(false);
            backupTransaction.Commit();

            var finalState = warnings.Count > 0 ? JobState.SucceededWithWarnings : JobState.Succeeded;
            Report(finalState, 100, CoreText.Get("Batch_Completed", outputPath));
            return new JobResult(
                finalState,
                outputPath,
                warnings,
                LapseAdjustments: lapseAdjustments,
                LapseChecks: lapseCheckSummaries);
        }
        catch (OperationCanceledException)
        {
            RollbackBackups();
            Report(JobState.Cancelled, currentPercent, CoreText.Get("Batch_Cancelled"));
            throw;
        }
        catch (JobSkippedException exception)
        {
            RollbackBackups();
            Report(JobState.Skipped, currentPercent, exception.Message);
            return new JobResult(JobState.Skipped, null, warnings, exception.Message);
        }
        catch (Exception exception)
        {
            RollbackBackups();
            Report(JobState.Failed, currentPercent, exception.Message);
            return new JobResult(JobState.Failed, null, warnings, exception.Message);
        }
    }

    private async Task<LapseSyncResult> RunLapseAsync(
        string mediaPath,
        string subtitlePath,
        string outputPath,
        MkvInspection sourceInspection,
        AppSettings settings,
        IReadOnlySet<int>? excludedSubtitleTrackIds,
        int progressPercent,
        IProgress<JobProgress>? progress,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        LapseReferenceSelection reference;
        try
        {
            reference = LapseReferenceSelector.Select(sourceInspection, settings, excludedSubtitleTrackIds);
            progress?.Report(new JobProgress(
                JobState.AnalyzingLapse,
                progressPercent,
                CoreText.Get("Batch_LapseReferenceSelected", reference.Description)));
        }
        catch (Exception exception)
        {
            return new LapseSyncResult(LapseVerdict.Failed, settings.LapseMode.ToString(), "—", null, null, null, 0, [], null, exception.Message);
        }
        var result = await _lapseSynchronizer.SynchronizeAsync(
            new LapseSyncRequest(
                mediaPath, subtitlePath, outputPath, settings.LapseMode,
                settings.LapseSplitPenalty, reference, settings.LapseConfidenceThreshold),
            onOutput,
            cancellationToken).ConfigureAwait(false);
        if (reference.SubtitleOrdinal is not null
            && string.Equals(result.Reference, "vad", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new JobProgress(
                JobState.AnalyzingLapse,
                progressPercent,
                CoreText.Get("Batch_LapseReferenceFallback", reference.Description)));
        }

        return result;
    }

    private static string DescribeLapseApplied(string target, LapseSyncResult result) =>
        CoreText.Get(
            "Batch_LapseApplied",
            target,
            result.Mode,
            result.Reference,
            FormatLapseConfidence(result.Confidence),
            result.OffsetMilliseconds ?? 0);

    private static string DescribeLapseWarning(string target, LapseSyncResult result) => result.Verdict switch
    {
        LapseVerdict.Unsure => CoreText.Get(
            "Batch_LapseUnsure",
            target,
            result.Mode,
            result.Reference,
            FormatLapseConfidence(result.Confidence),
            result.OffsetMilliseconds ?? 0),
        LapseVerdict.Nothing => CoreText.Get(
            "Batch_LapseNothing",
            target,
            result.Mode,
            result.Reference,
            FormatLapseConfidence(result.Confidence)),
        _ => CoreText.Get("Batch_LapseFailed", target, result.Error ?? "Unknown error")
    };

    private static string FormatLapseConfidence(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";

    private static string FormatLapseSeconds(double milliseconds) =>
        (milliseconds / 1000).ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatSignedLapseSeconds(double milliseconds) =>
        (milliseconds / 1000).ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);

    private static bool IsShiftMode(string mode) =>
        mode.EndsWith("/shifted", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("shifted", StringComparison.OrdinalIgnoreCase);

    private static string DescribeLapseCheckSummary(LapseCheckSummary check)
    {
        if (check.MaximumAdjustmentMilliseconds is not { } maximumAdjustment)
        {
            return CoreText.Get("Lapse_CheckSummaryInconclusive", check.Target);
        }

        return IsShiftMode(check.Mode) && check.OffsetMilliseconds is { } offsetMilliseconds
            ? CoreText.Get(
                "Lapse_CheckSummaryShifted",
                check.Target,
                FormatSignedLapseSeconds(offsetMilliseconds),
                FormatLapseSeconds(check.WarningThresholdSeconds * 1000))
            : CoreText.Get(
                "Lapse_CheckSummarySolid",
                check.Target,
                FormatLapseSeconds(maximumAdjustment),
                FormatLapseSeconds(check.WarningThresholdSeconds * 1000));
    }

    internal static string GetSubtitleSourceTagValue(ConversionPlan plan)
    {
        if (plan.AssSource != AssSourceKind.Existing)
        {
            return plan.SrtSource == SrtSourceKind.ConvertFromSmi ? "SMI" : "SRT";
        }

        return plan.SrtSource switch
        {
            SrtSourceKind.Existing => "ASS+SRT",
            SrtSourceKind.ConvertFromSmi => "ASS+SMI",
            _ => "ASS"
        };
    }

    private static void ValidateInputs(MediaSet media, ConversionPlan plan)
    {
        var required = new List<string?> { media.VideoPath };
        if (plan.AssSource == AssSourceKind.Existing)
        {
            required.Add(media.AssPath);
        }

        switch (plan.SrtSource)
        {
            case SrtSourceKind.Existing:
                required.Add(media.SrtPath);
                break;
            case SrtSourceKind.ConvertFromAss:
                required.Add(media.AssPath);
                break;
            case SrtSourceKind.ConvertFromSmi:
                required.Add(media.SmiPath);
                break;
        }

        var missing = required.FirstOrDefault(static path => string.IsNullOrWhiteSpace(path) || !File.Exists(path));
        if (missing is not null || required.Any(static path => path is null))
        {
            throw new FileNotFoundException(CoreText.Get("Batch_InputMovedOrDeleted"), missing);
        }
    }

    private static void AddSubtitleConversionWarnings(
        ICollection<string> warnings,
        SubtitleConversionResult result)
    {
        foreach (var warning in result.Warnings)
        {
            warnings.Add($"libse: {warning}");
        }
    }

    private static void ValidateGeneratedAudio(MkvInspection inspection, int? expectedChannels)
    {
        var audioTracks = inspection.Tracks
            .Where(static track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (audioTracks.Length != 1
            || !audioTracks[0].CodecId.Contains("AAC", StringComparison.OrdinalIgnoreCase)
            || (expectedChannels.HasValue && audioTracks[0].AudioChannels != expectedChannels))
        {
            throw new InvalidOperationException(CoreText.Get("Ffmpeg_OutputValidationFailed"));
        }
    }

    private async Task<IReadOnlyList<FontAttachmentFile>> ResolveAssFontAttachmentsAsync(
        string assPath,
        ConversionPlan plan,
        AppSettings settings,
        string workspacePath,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var assText = await ReadSubtitleTextAsync(assPath, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AssFontRequirement> requirements;
        try
        {
            requirements = AssFontNameExtractor.ExtractRequirements(assText);
        }
        catch (AssFontAnalysisException exception)
        {
            var warning = CoreText.Get("Batch_FontAnalysisError", exception.Message);
            warnings.Add(warning);
            throw new JobSkippedException(CoreText.Get("Batch_SkipNoOutput", warning));
        }

        if (requirements.Count == 0
            && plan.AssSource == AssSourceKind.ConvertFromSrt
            && settings.UseCustomAssStyle
            && AssStyleDefinition.TryParse(settings.AssStyleLine, out var configuredStyle))
        {
            requirements = [new AssFontRequirement(
                configuredStyle!.FontName,
                configuredStyle.Bold ? 700 : 400,
                configuredStyle.Italic)];
        }

        if (requirements.Count == 0)
        {
            var warning = CoreText.Get("Batch_FontNameMissing");
            warnings.Add(warning);
            throw new JobSkippedException(CoreText.Get("Batch_SkipFontAttachmentRequired", warning));
        }

        var attachments = new List<FontAttachmentFile>();
        var missingFontFamilies = new List<string>();
        var missingFontFamilySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requirements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstalledFontMatch? match;
            try
            {
                match = _installedFontResolver.Resolve(requirement);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                var warning = CoreText.Get("Batch_FontSearchError", requirement.FamilyName, exception.Message);
                warnings.Add(warning);
                throw new JobSkippedException(CoreText.Get("Batch_SkipNoOutput", warning));
            }

            if (match is null)
            {
                if (string.Equals(requirement.FamilyName.Trim(), "SubMux Sans", StringComparison.OrdinalIgnoreCase))
                {
                    var bundledFontPath = await ExtractBundledSubMuxFontAsync(workspacePath, cancellationToken)
                        .ConfigureAwait(false);
                    match = new InstalledFontMatch(
                        new FontAttachmentFile(bundledFontPath, "font/otf", "SubMuxSans-Medium.otf"),
                        InstalledFontMatchKind.Compatibility,
                        500,
                        false,
                        "SubMux Sans");
                }
                else
                {
                    var familyName = requirement.FamilyName.Trim();
                    if (missingFontFamilySet.Add(familyName))
                    {
                        missingFontFamilies.Add(familyName);
                    }
                    continue;
                }
            }

            if (match.MatchKind == InstalledFontMatchKind.RegistryAlias)
            {
                warnings.Add(CoreText.Get(
                    "Batch_FontRegistryAlias",
                    requirement.FamilyName,
                    match.InternalName));
            }

            attachments.Add(match.File);
        }

        if (missingFontFamilies.Count > 0)
        {
            var warning = CoreText.Get(
                "Batch_FontsNotFound",
                missingFontFamilies.Count,
                string.Join(", ", missingFontFamilies));
            warnings.Add(warning);
            throw new JobSkippedException(CoreText.Get("Batch_SkipNoOutput", warning));
        }

        return await DeduplicateAndNameFontAttachmentsAsync(attachments, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<string> ExtractBundledSubMuxFontAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        const string resourceName = "SubMuxBatch.Core.Resources.SubMuxSans-Medium.otf";
        await using var source = typeof(BatchProcessor).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(CoreText.Get("Batch_BundledFontMissing"));
        var destination = Path.Combine(workspacePath, "SubMuxSans-Medium.otf");
        await using var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    internal static async Task<IReadOnlyList<FontAttachmentFile>> DeduplicateAndNameFontAttachmentsAsync(
        IEnumerable<FontAttachmentFile> attachments,
        CancellationToken cancellationToken)
    {
        var candidates = attachments
            .DistinctBy(static attachment => Path.GetFullPath(attachment.FilePath), StringComparer.OrdinalIgnoreCase)
            .OrderBy(static attachment => attachment.SourceFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static attachment => attachment.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unique = new List<(FontAttachmentFile Attachment, string Hash)>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attachment in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                attachment.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            var contentKey = $"{stream.Length}:{hash}";
            if (hashes.Add(contentKey))
            {
                unique.Add((attachment, hash));
            }
        }

        var result = new List<FontAttachmentFile>(unique.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (attachment, hash) in unique)
        {
            var name = attachment.SourceFileName;
            if (!usedNames.Add(name))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var extension = Path.GetExtension(name);
                var prefixLength = 8;
                do
                {
                    name = $"{stem}-{hash[..Math.Min(prefixLength, hash.Length)].ToLowerInvariant()}{extension}";
                    prefixLength += 4;
                }
                while (!usedNames.Add(name));
            }

            result.Add(name.Equals(attachment.SourceFileName, StringComparison.OrdinalIgnoreCase)
                ? attachment
                : attachment with { AttachmentName = name });
        }

        return result;
    }

    private static async Task<string> ReadSubtitleTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }

    private static string CommitToAvailableOutput(string partialPath, string preferredOutputPath)
    {
        var directory = System.IO.Path.GetDirectoryName(preferredOutputPath)
            ?? throw new ArgumentException("The output path must include a directory.", nameof(preferredOutputPath));
        var fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(preferredOutputPath);
        var extension = System.IO.Path.GetExtension(preferredOutputPath);

        for (var suffix = 0; suffix < int.MaxValue; suffix++)
        {
            var candidate = suffix == 0
                ? preferredOutputPath
                : System.IO.Path.Combine(directory, $"{fileNameWithoutExtension} ({suffix}){extension}");

            try
            {
                File.Move(partialPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // The existing candidate belongs to the user or another concurrent
                // job. Keep the partial file and try the next suffix.
            }
        }

        throw new IOException("No available output filename could be allocated.");
    }

    internal static void ValidateCommittedOutputFile(string outputPath, long expectedLength)
    {
        var file = new FileInfo(outputPath);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidOperationException(CoreText.Get("Batch_CommittedOutputMissing", outputPath));
        }

        if (file.Length != expectedLength)
        {
            throw new InvalidOperationException(
                CoreText.Get("Batch_CommittedOutputSizeMismatch", expectedLength, file.Length));
        }
    }

    private sealed class JobWorkspace : IAsyncDisposable
    {
        private readonly string _parent;
        private int _disposed;

        private JobWorkspace(string parent, string path)
        {
            _parent = parent;
            Path = path;
        }

        public string Path { get; }

        public static JobWorkspace Create(string outputDirectory)
        {
            var parent = System.IO.Path.GetFullPath(outputDirectory);
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var id = Guid.NewGuid().ToString("N")[..12];
                var path = System.IO.Path.Combine(parent, $"{WorkspaceNaming.CurrentPrefix}{id}");
                if (Directory.Exists(path) || File.Exists(path))
                {
                    continue;
                }

                Directory.CreateDirectory(path);
                return new JobWorkspace(parent, path);
            }

            throw new IOException(CoreText.Get("Batch_CreateWorkspaceFailed"));
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            try
            {
                var resolved = System.IO.Path.GetFullPath(Path);
                var expectedParent = System.IO.Path.GetFullPath(_parent)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                    + System.IO.Path.DirectorySeparatorChar;
                var leaf = System.IO.Path.GetFileName(resolved);
                if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
                    && leaf.StartsWith(WorkspaceNaming.CurrentPrefix, StringComparison.Ordinal)
                    && Directory.Exists(resolved))
                {
                    Directory.Delete(resolved, recursive: true);
                }
            }
            catch
            {
                // A locked temporary file is harmless and can be removed on the next cleanup pass.
            }

            return ValueTask.CompletedTask;
        }
    }
}
