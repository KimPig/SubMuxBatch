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
    IInstalledFontResolver? installedFontResolver = null)
{
    private readonly IInstalledFontResolver _installedFontResolver =
        installedFontResolver ?? InstalledFontResolver.System;

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

        if (!dependencies.IsReady || dependencies.MkvMerge.Path is null || dependencies.SeConv.Path is null)
        {
            return new JobResult(JobState.Failed, null, plan.Warnings, CoreText.Get("Batch_DependenciesMissing"));
        }

        var warnings = plan.Warnings.ToList();
        var currentState = JobState.Ready;
        var currentPercent = 0;

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

        try
        {
            settings.Validate();
            ValidateInputs(media, plan);

            var preferredOutputPath = Path.Combine(
                media.Key.DirectoryPath,
                OutputFileNaming.Create(media.VideoPath, settings.OutputPrefix));

            await using var workspace = JobWorkspace.Create(media.Key.DirectoryPath);
            var seConv = new SeConvClient(dependencies.SeConv.Path, processRunner);
            var mkvMerge = new MkvMergeClient(dependencies.MkvMerge.Path, processRunner);
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

            string finalSrt;
            switch (plan.SrtSource)
            {
                case SrtSourceKind.Existing:
                    finalSrt = media.SrtPath!;
                    break;

                case SrtSourceKind.ConvertFromAss:
                    Report(JobState.ConvertingAssToSrt, 8, CoreText.Get("Batch_ConvertAssToSrt"));
                    finalSrt = Path.Combine(workspace.Path, "secondary.srt");
                    var assToSrtResult = await seConv.ConvertAsync(
                        media.AssPath!,
                        finalSrt,
                        SubtitleOutputFormat.SubRip,
                        null,
                        settings.PlayResX,
                        settings.PlayResY,
                        LogToolOutput,
                        cancellationToken).ConfigureAwait(false);
                    AddSeConvWarnings(warnings, assToSrtResult);
                    break;

                case SrtSourceKind.ConvertFromSmi:
                    Report(JobState.ConvertingSmiToSrt, 8, CoreText.Get("Batch_ConvertSmiToSrt"));
                    finalSrt = Path.Combine(workspace.Path, "secondary.srt");
                    var smiToSrtResult = await seConv.ConvertAsync(
                        media.SmiPath!,
                        finalSrt,
                        SubtitleOutputFormat.SubRip,
                        null,
                        settings.PlayResX,
                        settings.PlayResY,
                        LogToolOutput,
                        cancellationToken).ConfigureAwait(false);
                    AddSeConvWarnings(warnings, smiToSrtResult);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(plan.SrtSource));
            }

            cancellationToken.ThrowIfCancellationRequested();

            string finalAss;
            switch (plan.AssSource)
            {
                case AssSourceKind.Existing:
                    finalAss = media.AssPath!;
                    break;

                case AssSourceKind.ConvertFromSrt:
                    Report(JobState.ConvertingSrtToAss, 24, CoreText.Get("Batch_ConvertSrtToAss"));
                    finalAss = Path.Combine(workspace.Path, "primary.ass");
                    var compatibleSrt = Path.Combine(workspace.Path, "ass-compatible.srt");
                    await SubtitleCompatibilityNormalizer.PrepareSrtForAssAsync(
                        finalSrt,
                        compatibleSrt,
                        cancellationToken).ConfigureAwait(false);
                    string? stylePath = null;
                    if (settings.UseCustomAssStyle)
                    {
                        stylePath = Path.Combine(workspace.Path, "default-style.ass");
                        await File.WriteAllTextAsync(
                            stylePath,
                            AssStyleTemplateWriter.Create(settings),
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                            cancellationToken).ConfigureAwait(false);
                    }

                    var srtToAssResult = await seConv.ConvertAsync(
                        compatibleSrt,
                        finalAss,
                        SubtitleOutputFormat.AdvancedSubStationAlpha,
                        stylePath,
                        settings.PlayResX,
                        settings.PlayResY,
                        LogToolOutput,
                        cancellationToken).ConfigureAwait(false);
                    AddSeConvWarnings(warnings, srtToAssResult);

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
            IReadOnlyList<FontAttachmentFile> fontAttachments = [];
            if (settings.AttachAssStyleFonts)
            {
                Report(JobState.Verifying, 32, CoreText.Get("Batch_FindFonts"));
                fontAttachments = await ResolveAssFontAttachmentsAsync(
                    finalAss,
                    plan,
                    settings,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
            }

            Report(JobState.Verifying, 34, CoreText.Get("Batch_InspectSource"));
            var sourceInspection = await mkvMerge.InspectAsync(media.VideoPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var partialPath = Path.Combine(workspace.Path, "output.partial.mkv");
            Report(JobState.Muxing, 38, CoreText.Get("Batch_MuxSubtitles"));
            var muxResult = await mkvMerge.MuxAsync(
                media.VideoPath,
                finalAss,
                finalSrt,
                partialPath,
                muxPercent =>
                {
                    var totalPercent = 38 + (int)Math.Round(muxPercent * 0.54);
                    Report(JobState.Muxing, totalPercent, CoreText.Get("Batch_MuxProgress", muxPercent));
                },
                LogToolOutput,
                cancellationToken,
                removeExistingSubtitles: settings.RemoveExistingSubtitles,
                removeExistingFontAttachments: settings.RemoveExistingFontAttachments,
                removeChapters: settings.RemoveChapters,
                keepOnlyAudioLanguage: settings.FilterAudioTracksByLanguage
                    ? settings.SelectedAudioLanguage
                    : null,
                fontAttachments: fontAttachments,
                globalTagsPath: globalTagsPath).ConfigureAwait(false);

            Report(JobState.Verifying, 94, CoreText.Get("Batch_VerifyOutput"));
            var outputInspection = await mkvMerge.InspectAsync(partialPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var validationErrors = MkvMergeClient.ValidateOutput(
                sourceInspection,
                outputInspection,
                removeExistingSubtitles: settings.RemoveExistingSubtitles,
                removeExistingFontAttachments: settings.RemoveExistingFontAttachments,
                removeChapters: settings.RemoveChapters,
                keepOnlyAudioLanguage: settings.FilterAudioTracksByLanguage
                    ? settings.SelectedAudioLanguage
                    : null,
                addedFontAttachments: fontAttachments);
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
            var committedInspection = await mkvMerge.InspectAsync(
                    outputPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var committedValidationErrors = MkvMergeClient.ValidateOutput(
                sourceInspection,
                committedInspection,
                removeExistingSubtitles: settings.RemoveExistingSubtitles,
                removeExistingFontAttachments: settings.RemoveExistingFontAttachments,
                removeChapters: settings.RemoveChapters,
                keepOnlyAudioLanguage: settings.FilterAudioTracksByLanguage
                    ? settings.SelectedAudioLanguage
                    : null,
                addedFontAttachments: fontAttachments);
            if (committedValidationErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    CoreText.Get("Batch_OutputValidationFailed")
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, committedValidationErrors));
            }

            Report(JobState.Verifying, 99, CoreText.Get("Batch_CleanupWorkspace"));
            await workspace.DisposeAsync().ConfigureAwait(false);

            var finalState = warnings.Count > 0 ? JobState.SucceededWithWarnings : JobState.Succeeded;
            Report(finalState, 100, CoreText.Get("Batch_Completed", outputPath));
            return new JobResult(finalState, outputPath, warnings);
        }
        catch (OperationCanceledException)
        {
            Report(JobState.Cancelled, currentPercent, CoreText.Get("Batch_Cancelled"));
            throw;
        }
        catch (JobSkippedException exception)
        {
            Report(JobState.Skipped, currentPercent, exception.Message);
            return new JobResult(JobState.Skipped, null, warnings, exception.Message);
        }
        catch (Exception exception)
        {
            Report(JobState.Failed, currentPercent, exception.Message);
            return new JobResult(JobState.Failed, null, warnings, exception.Message);
        }
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

    private static void AddSeConvWarnings(ICollection<string> warnings, SeConvResult result)
    {
        foreach (var warning in result.Warnings)
        {
            warnings.Add($"Subtitle Edit: {warning}");
        }
    }

    private async Task<IReadOnlyList<FontAttachmentFile>> ResolveAssFontAttachmentsAsync(
        string assPath,
        ConversionPlan plan,
        AppSettings settings,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var assText = await ReadSubtitleTextAsync(assPath, cancellationToken).ConfigureAwait(false);
        var fontNames = AssFontNameExtractor.Extract(assText).ToList();
        if (fontNames.Count == 0
            && plan.AssSource == AssSourceKind.ConvertFromSrt
            && settings.UseCustomAssStyle
            && AssStyleDefinition.TryParse(settings.AssStyleLine, out var configuredStyle))
        {
            fontNames.Add(configuredStyle!.FontName);
        }

        if (fontNames.Count == 0)
        {
            var warning = CoreText.Get("Batch_FontNameMissing");
            warnings.Add(warning);
            throw new JobSkippedException(CoreText.Get("Batch_SkipFontAttachmentRequired", warning));
        }

        var attachments = new List<FontAttachmentFile>();
        foreach (var fontName in fontNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FontAttachmentFile> matches;
            try
            {
                matches = _installedFontResolver.FindByFamilyName(fontName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                var warning = CoreText.Get("Batch_FontSearchError", fontName, exception.Message);
                warnings.Add(warning);
                throw new JobSkippedException(CoreText.Get("Batch_SkipNoOutput", warning));
            }

            if (matches.Count == 0)
            {
                var warning = CoreText.Get("Batch_FontNotFound", fontName);
                warnings.Add(warning);
                throw new JobSkippedException(CoreText.Get("Batch_SkipNoOutput", warning));
            }

            attachments.AddRange(matches);
        }

        return attachments
            .DistinctBy(static attachment => attachment.FileName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static attachment => attachment.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
            var path = System.IO.Path.Combine(parent, $"{WorkspaceNaming.CurrentPrefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new JobWorkspace(parent, path);
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
