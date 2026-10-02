using System.Text;
using System.Xml.Linq;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Fonts;
using SubMuxBatch.Core.Localization;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Processing;

public sealed class MaintenanceProcessor(
    IProcessRunner processRunner,
    IInstalledFontResolver? fontResolver = null,
    IAudioTranscoder? audioTranscoder = null)
{
    private readonly IInstalledFontResolver _fontResolver = fontResolver ?? InstalledFontResolver.System;
    private readonly IAudioTranscoder _audioTranscoder = audioTranscoder ?? new BundledFfmpegAudioTranscoder(processRunner);

    public async Task<JobResult> ProcessAsync(
        string sourcePath,
        AppSettings settings,
        DependencyReport dependencies,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        if (!string.Equals(Path.GetExtension(sourcePath), ".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return new JobResult(JobState.Skipped, null, warnings, CoreText.Get("Maintenance_MkvOnly"));
        }
        if (!dependencies.IsReady || dependencies.MkvMerge.Path is null)
        {
            return new JobResult(JobState.Failed, null, warnings, CoreText.Get("Maintenance_MissingTools"));
        }

        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        var workspace = Path.Combine(sourceDirectory, $"{WorkspaceNaming.CurrentPrefix}maintenance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            void Report(JobState state, int percent, string message) =>
                progress?.Report(new JobProgress(state, percent, message));

            var mkvMerge = new MkvMergeClient(dependencies.MkvMerge.Path, processRunner);
            Report(JobState.Verifying, 4, CoreText.Get("Maintenance_Analyze"));
            var sourceInspection = await mkvMerge.InspectAsync(sourcePath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
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
            var refreshStyleFonts = false;
            string? sourceKind = null;
            var assTracks = sourceInspection.Tracks.Where(IsAssTrack).ToArray();
            var candidates = new List<AssCandidate>();
            if ((settings.MaintenanceUpdateAssStyle || settings.MaintenanceRefreshTags) && assTracks.Length > 0)
            {
                Report(JobState.UpdatingAssStyle, 12, CoreText.Get("Maintenance_CheckAssStyle"));
                foreach (var track in assTracks)
                {
                    if (track.Id is null) continue;
                    var extractedPath = Path.Combine(workspace, $"subtitle-{track.Id}.ass");
                    await ExtractTrackAsync(mkvExtractPath, sourcePath, track.Id.Value, extractedPath, cancellationToken)
                        .ConfigureAwait(false);
                    var text = await File.ReadAllTextAsync(extractedPath, cancellationToken).ConfigureAwait(false);
                    candidates.Add(new AssCandidate(
                        track,
                        extractedPath,
                        text,
                        MatchesLegacyStyle(text, settings.MaintenanceLegacyAssStyles),
                        SubMuxMetadata.ReadAssSourceMarker(text)));
                }

                var internallyMarked = candidates.Where(static candidate => candidate.InternalSource is not null).ToArray();
                AssCandidate? selected;
                if (internallyMarked.Length > 0)
                {
                    selected = SelectStyledTrack(internallyMarked);
                    sourceKind = selected?.InternalSource;
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
                        Report(
                            JobState.UpdatingAssStyle,
                            18,
                            CoreText.Get("Maintenance_LegacyStyleMatched", selected.Track.Id?.ToString() ?? "?"));
                    }
                    else
                    {
                        selected = SelectStyledTrack(candidates);
                        sourceKind = SubMuxMetadata.LegacyAssOrUnknownSource;
                        warnings.Add(CoreText.Get("Maintenance_AmbiguousAss"));
                    }
                }

                if (selected is not null)
                {
                    var shouldUpdateStyle = settings.MaintenanceUpdateAssStyle && IsGeneratedAssSource(sourceKind);
                    refreshStyleFonts = shouldUpdateStyle;
                    var updated = shouldUpdateStyle
                        ? ReplaceDefaultStyle(selected.Text, settings.AssStyleLine)
                        : selected.Text;
                    if (sourceKind is not null)
                    {
                        updated = SubMuxMetadata.AddAssSourceMarker(updated, sourceKind);
                    }

                    if (shouldUpdateStyle || !string.Equals(updated, selected.Text, StringComparison.Ordinal))
                    {
                        replacementAssPath = Path.Combine(workspace, "maintained.ass");
                        await File.WriteAllTextAsync(
                                replacementAssPath,
                                updated,
                                new UTF8Encoding(false),
                                cancellationToken)
                            .ConfigureAwait(false);
                        replacementAssTrack = selected.Track;
                    }

                    Report(
                        JobState.UpdatingAssStyle,
                        19,
                        CoreText.Get("Maintenance_AssSourceMarked", sourceKind ?? "—"));
                    if (shouldUpdateStyle)
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
                    else if (settings.MaintenanceUpdateAssStyle)
                    {
                        Report(JobState.Verifying, 20, CoreText.Get("Maintenance_OriginalAssRetained"));
                    }
                }
                else if (sourceKind is null)
                {
                    warnings.Add(CoreText.Get("Maintenance_AmbiguousAss"));
                }
            }

            if (settings.MaintenanceUpdateAssStyle && assTracks.Length == 0)
            {
                sourceKind = null;
            }

            IReadOnlyList<FontAttachmentFile> fontAttachments = [];
            var removeExistingStyleFonts = false;
            if (refreshStyleFonts && replacementAssPath is not null && settings.MaintenanceUpdateFonts)
            {
                Report(JobState.Verifying, 24, CoreText.Get("Maintenance_PrepareFont"));
                var resolvedFonts = await ResolveStyleFontsAsync(replacementAssPath, settings, workspace, cancellationToken)
                    .ConfigureAwait(false);
                fontAttachments = resolvedFonts.Attachments;
                Report(
                    JobState.Verifying,
                    26,
                    ProcessingDecisionFormatter.DescribeFontAttachments(fontAttachments));
                removeExistingStyleFonts = assTracks.Length == 1 && resolvedFonts.AllRequirementsResolved;
                if (!removeExistingStyleFonts)
                {
                    warnings.Add(CoreText.Get("Maintenance_PreserveFonts"));
                }
            }

            AudioMuxPlan? audioMuxPlan = null;
            if (settings.MaintenanceApplyAudioSettings
                && (settings.ConvertAudioToAac || settings.FilterAudioTracksByLanguage))
            {
                var conversionPlan = AudioConversionPlanner.Create(sourceInspection, settings);
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
            if (settings.MaintenanceRefreshTags)
            {
                updatedTagsPath = Path.Combine(workspace, "tags.xml");
                var updatedTags = UpdateTags(tagsXml);
                await File.WriteAllTextAsync(updatedTagsPath, updatedTags, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
                Report(
                    JobState.Verifying,
                    54,
                    CoreText.Get(
                        "Maintenance_TagsRefreshed",
                        SubMuxMetadata.GetApplicationVersion()));
            }

            var hasAudioChanges = audioMuxPlan?.GeneratedTracks.Count > 0
                                  || audioMuxPlan?.SourceDefaultTrackOverrides.Count > 0
                                  || (audioMuxPlan is not null
                                      && audioMuxPlan.RetainedSourceTrackIds.Count
                                      != sourceInspection.Tracks.Count(static track =>
                                          string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase)));
            if (replacementAssPath is null && !hasAudioChanges && updatedTagsPath is null)
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
            var muxResult = await mkvMerge.MaintainAsync(
                sourcePath, partialPath, replacementAssPath, replacementAssTrack, fontAttachments,
                removeExistingStyleFonts,
                updatedTagsPath, audioMuxPlan,
                percent => Report(JobState.Muxing, 58 + percent * 35 / 100, CoreText.Get("Maintenance_MuxProgress", percent)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            warnings.AddRange(muxResult.Warnings.Select(static warning => $"mkvmerge: {warning}"));

            Report(JobState.Verifying, 95, CoreText.Get("Maintenance_Verify"));
            var outputInspection = await mkvMerge.InspectAsync(partialPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            ValidateOutput(sourceInspection, outputInspection, replacementAssPath is not null);
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

                if (new FileInfo(outputPath).Length <= 0)
                {
                    throw new InvalidOperationException(CoreText.Get("Maintenance_EmptyOutput"));
                }
                var committedInspection = await mkvMerge.InspectAsync(outputPath, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                ValidateOutput(sourceInspection, committedInspection, replacementAssPath is not null);
                Report(JobState.Verifying, 99, ProcessingDecisionFormatter.DescribeVerifiedOutput(committedInspection));
                if (originalBackupPath is not null && File.Exists(originalBackupPath))
                {
                    File.Delete(originalBackupPath);
                }
            }
            catch
            {
                if (originalBackupPath is not null && File.Exists(originalBackupPath))
                {
                    RestoreOriginal(sourcePath, originalBackupPath);
                    Report(JobState.Verifying, 99, CoreText.Get("Maintenance_SourceRestored"));
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
            TryDeleteWorkspace(workspace, sourceDirectory);
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
    {
        var result = await processRunner.RunAsync(
            new ProcessRequest(mkvExtract, [source, "tracks", $"{id}:{output}"], Path.GetDirectoryName(output)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode >= 2 || !File.Exists(output))
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
        string? InternalSource);

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

    private static string UpdateTags(string xml)
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
                     names.Contains(simple.Element("Name")?.Value, StringComparer.OrdinalIgnoreCase)).ToArray())
            simple.Remove();
        tag.Add(CreateSimple(SubMuxMetadata.VersionTagName, SubMuxMetadata.GetApplicationVersion()));
        tag.Add(CreateSimple(SubMuxMetadata.ProcessedTagName, SubMuxMetadata.ProcessedValue));
        return document.ToString();
    }

    private static bool IsGeneratedAssSource(string? source) =>
        source is "SRT" or "SMI" or SubMuxMetadata.LegacySrtOrSmiSource;

    private static XElement CreateSimple(string name, string value) =>
        new("Simple", new XElement("Name", name), new XElement("String", value));

    private static string ResolveMkvExtractPath(string mkvMergePath)
    {
        var path = Path.Combine(Path.GetDirectoryName(mkvMergePath)!, OperatingSystem.IsWindows() ? "mkvextract.exe" : "mkvextract");
        if (!File.Exists(path)) throw new FileNotFoundException(CoreText.Get("Maintenance_MkvExtractMissing"), path);
        return path;
    }

    private static void ValidateOutput(MkvInspection source, MkvInspection output, bool replacedAss)
    {
        var sourceVideo = source.Tracks.Count(static track => string.Equals(track.Type, "video", StringComparison.OrdinalIgnoreCase));
        var outputVideo = output.Tracks.Count(static track => string.Equals(track.Type, "video", StringComparison.OrdinalIgnoreCase));
        if (sourceVideo != outputVideo || outputVideo == 0) throw new InvalidOperationException(CoreText.Get("Maintenance_VideoValidationFailed"));
        if (replacedAss && !output.Tracks.Any(IsAssTrack)) throw new InvalidOperationException(CoreText.Get("Maintenance_AssValidationFailed"));
        if (source.DurationNanoseconds is > 0 && output.DurationNanoseconds is > 0
            && Math.Abs(source.DurationNanoseconds.Value - output.DurationNanoseconds.Value) > 2_000_000_000L)
            throw new InvalidOperationException(CoreText.Get("Maintenance_DurationValidationFailed"));
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
}
