using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Media;

public sealed record MetadataBackupSource(
    string FileName,
    string Extension,
    long SizeBytes,
    DateTime LastWriteTimeUtc);

public sealed record MetadataBackupDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string SubMuxBatchVersion,
    MetadataBackupSource Source,
    JsonElement MkvMergeIdentification,
    IReadOnlyList<MediaInfoRawStream> MediaInfo,
    string? MatroskaTagsXml,
    string? MatroskaChaptersXml);

public sealed class MetadataBackupService(
    IProcessRunner processRunner,
    IMediaInfoRawReader? mediaInfoRawReader = null)
{
    public const string BackupDirectoryName = ".submux-backup";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    private static readonly HashSet<string> ReservedWindowsFileNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly IMediaInfoRawReader _mediaInfoRawReader =
        mediaInfoRawReader ?? new MediaInfoClient();

    public async Task<string> BackupMetadataAsync(
        string sourcePath,
        string mkvMergePath,
        MkvIdentification identification,
        CancellationToken cancellationToken = default)
    {
        var source = ValidateSource(sourcePath, mkvMergePath, identification, cancellationToken);

        JsonElement identificationJson;
        try
        {
            using var document = JsonDocument.Parse(identification.Json);
            identificationJson = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(CoreText.Get("MetadataBackup_InvalidIdentification"), exception);
        }

        var mediaInfo = await _mediaInfoRawReader
            .ReadRawReportAsync(source.FullName, cancellationToken)
            .ConfigureAwait(false);

        string? tagsXml = null;
        string? chaptersXml = null;
        if (IsMatroska(identification.Inspection.ContainerType))
        {
            var mkvExtractPath = ResolveMkvExtractPath(mkvMergePath);
            EnsureMkvExtractExists(mkvExtractPath);
            tagsXml = await ExtractXmlAsync(
                mkvExtractPath,
                source.FullName,
                "tags",
                cancellationToken).ConfigureAwait(false);
            chaptersXml = await ExtractXmlAsync(
                mkvExtractPath,
                source.FullName,
                "chapters",
                cancellationToken).ConfigureAwait(false);
        }

        var backup = new MetadataBackupDocument(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            SubMuxMetadata.GetApplicationVersion(),
            new MetadataBackupSource(
                source.Name,
                source.Extension,
                source.Length,
                source.LastWriteTimeUtc),
            identificationJson,
            mediaInfo,
            tagsXml,
            chaptersXml);

        var videoBackupDirectory = CreateVideoBackupDirectory(source);
        var destination = GetAvailableFilePath(videoBackupDirectory, "metadata.json");
        var temporaryPath = Path.Combine(
            videoBackupDirectory,
            $".submux-backup-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    backup,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destination, overwrite: false);
            return destination;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public async Task<IReadOnlyList<string>> BackupSubtitlesAsync(
        string sourcePath,
        string mkvMergePath,
        MkvIdentification identification,
        CancellationToken cancellationToken = default)
    {
        var source = ValidateSource(sourcePath, mkvMergePath, identification, cancellationToken);
        var subtitleTracks = identification.Inspection.Tracks
            .Where(static track => IsTrackType(track, "subtitles"))
            .ToArray();
        var buttonTrackCount = identification.Inspection.Tracks.Count(
            static track => IsTrackType(track, "buttons"));
        if (subtitleTracks.Length == 0 && buttonTrackCount == 0)
        {
            return [];
        }

        var videoBackupDirectory = CreateVideoBackupDirectory(source);
        var trackIds = subtitleTracks.Select(static track => track.Id
            ?? throw new InvalidOperationException(CoreText.Get("MetadataBackup_TrackIdMissing", "subtitle")));
        var subtitlePath = await CreateTrackSidecarAsync(
            source,
            mkvMergePath,
            videoBackupDirectory,
            "subtitles.mks",
            trackIds,
            isAudio: false,
            cancellationToken).ConfigureAwait(false);
        return [subtitlePath];
    }

    public async Task<IReadOnlyList<string>> BackupAttachmentsAsync(
        string sourcePath,
        string mkvMergePath,
        MkvIdentification identification,
        CancellationToken cancellationToken = default)
    {
        var source = ValidateSource(sourcePath, mkvMergePath, identification, cancellationToken);
        var attachments = identification.Inspection.Attachments;
        if (attachments.Count == 0)
        {
            return [];
        }

        var videoBackupDirectory = CreateVideoBackupDirectory(source);
        var mkvExtractPath = ResolveMkvExtractPath(mkvMergePath);
        EnsureMkvExtractExists(mkvExtractPath);
        var attachmentSource = source.FullName;
        var extractionAttachments = attachments;
        string? temporaryAttachmentSource = null;
        try
        {
            if (!IsMatroska(identification.Inspection.ContainerType))
            {
                temporaryAttachmentSource = Path.Combine(
                    videoBackupDirectory,
                    $".submux-attachment-source-{Guid.NewGuid():N}.mkv");
                await CreateAttachmentSourceAsync(
                    source,
                    mkvMergePath,
                    temporaryAttachmentSource,
                    cancellationToken).ConfigureAwait(false);
                var temporaryInspection = await new MkvMergeClient(mkvMergePath, processRunner)
                    .InspectAsync(temporaryAttachmentSource, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                attachmentSource = temporaryAttachmentSource;
                extractionAttachments = temporaryInspection.Attachments;
                if (extractionAttachments.Count == 0)
                {
                    throw new InvalidOperationException(CoreText.Get("MetadataBackup_AttachmentsNotPreserved"));
                }
            }

            return await ExtractAttachmentsAsync(
                mkvExtractPath,
                attachmentSource,
                extractionAttachments,
                Path.Combine(videoBackupDirectory, "attachments"),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (temporaryAttachmentSource is not null)
            {
                TryDelete(temporaryAttachmentSource);
            }
        }
    }

    public async Task<string?> BackupExcludedAudioTracksAsync(
        string sourcePath,
        string mkvMergePath,
        MkvIdentification identification,
        AudioTrackLanguage retainedLanguage,
        CancellationToken cancellationToken = default)
    {
        var source = ValidateSource(sourcePath, mkvMergePath, identification, cancellationToken);
        var audioTracks = identification.Inspection.Tracks
            .Where(static track => IsTrackType(track, "audio"))
            .ToArray();
        if (audioTracks.Length <= 1)
        {
            return null;
        }

        var retainedTracks = audioTracks
            .Where(track => MatchesAudioLanguage(track, retainedLanguage))
            .ToArray();
        if (retainedTracks.Length == 0)
        {
            // The mux operation reports this as a skipped job. Nothing is treated as
            // excluded until a requested-language track can actually be retained.
            return null;
        }

        var excludedTrackIds = audioTracks
            .Where(track => !MatchesAudioLanguage(track, retainedLanguage))
            .Select(static track => track.Id
                ?? throw new InvalidOperationException(CoreText.Get("MetadataBackup_TrackIdMissing", "audio")))
            .ToArray();
        if (excludedTrackIds.Length == 0)
        {
            return null;
        }

        var videoBackupDirectory = CreateVideoBackupDirectory(source);
        return await CreateTrackSidecarAsync(
            source,
            mkvMergePath,
            videoBackupDirectory,
            "excluded-audio.mka",
            excludedTrackIds,
            isAudio: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CreateTrackSidecarAsync(
        FileInfo source,
        string mkvMergePath,
        string destinationDirectory,
        string fileName,
        IEnumerable<int> trackIds,
        bool isAudio,
        CancellationToken cancellationToken)
    {
        var destination = GetAvailableFilePath(destinationDirectory, fileName);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".submux-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        var ids = string.Join(",", trackIds);
        var arguments = new List<string>
        {
            "--ui-language", "en",
            "-o", temporaryPath,
            "--no-video",
            "--no-attachments",
            "--no-chapters",
            "--no-global-tags"
        };
        if (isAudio)
        {
            arguments.Add("--no-buttons");
            arguments.Add("--no-subtitles");
            arguments.Add("--audio-tracks");
            arguments.Add(ids);
        }
        else
        {
            arguments.Add("--no-audio");
            if (ids.Length > 0)
            {
                arguments.Add("--subtitle-tracks");
                arguments.Add(ids);
            }
        }
        arguments.Add(source.FullName);

        try
        {
            await RunMkvMergeBackupAsync(
                mkvMergePath,
                arguments,
                source.DirectoryName,
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destination, overwrite: false);
            return destination;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task CreateAttachmentSourceAsync(
        FileInfo source,
        string mkvMergePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await RunMkvMergeBackupAsync(
            mkvMergePath,
            [
                "--ui-language", "en",
                "-o", outputPath,
                "--no-video",
                "--no-audio",
                "--no-subtitles",
                "--no-buttons",
                "--no-chapters",
                "--no-global-tags",
                source.FullName
            ],
            source.DirectoryName,
            outputPath,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunMkvMergeBackupAsync(
        string mkvMergePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new ProcessRequest(mkvMergePath, arguments, workingDirectory),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode >= 2)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException(CoreText.Get(
                "MetadataBackup_MuxFailed",
                result.ExitCode,
                details.Trim()));
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException(CoreText.Get("MetadataBackup_OutputMissing"));
        }
    }

    private async Task<string?> ExtractXmlAsync(
        string mkvExtractPath,
        string sourcePath,
        string mode,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new ProcessRequest(
                mkvExtractPath,
                [sourcePath, mode],
                Path.GetDirectoryName(sourcePath)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode >= 2)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException(CoreText.Get(
                "MetadataBackup_ExtractionFailed",
                mode,
                result.ExitCode,
                details.Trim()));
        }

        var xml = result.StandardOutput.TrimStart('\uFEFF').Trim();
        return xml.Length == 0 ? null : xml;
    }

    private async Task<IReadOnlyList<string>> ExtractAttachmentsAsync(
        string mkvExtractPath,
        string sourcePath,
        IReadOnlyList<MkvAttachmentInfo> attachments,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var parentDirectory = Path.GetDirectoryName(destinationDirectory)
                              ?? throw new InvalidOperationException(CoreText.Get("MetadataBackup_SourceDirectoryMissing"));
        var extractionDirectory = Path.Combine(
            parentDirectory,
            $".submux-attachments-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(extractionDirectory);

        try
        {
            var arguments = new List<string> { sourcePath, "attachments" };
            var extractedFiles = new List<(string ExtractionPath, string DestinationName)>(attachments.Count);
            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < attachments.Count; index++)
            {
                var attachment = attachments[index];
                if (attachment.Id is not int id)
                {
                    throw new InvalidOperationException(CoreText.Get(
                        "MetadataBackup_AttachmentIdMissing",
                        attachment.FileName ?? (index + 1).ToString()));
                }

                var safeName = MakeSafeAttachmentName(attachment.FileName, id);
                var extractionPath = GetAvailableFilePath(
                    extractionDirectory,
                    safeName,
                    reservedNames);
                reservedNames.Add(Path.GetFileName(extractionPath));
                arguments.Add($"{id}:{extractionPath}");
                extractedFiles.Add((extractionPath, safeName));
            }

            var result = await processRunner.RunAsync(
                new ProcessRequest(mkvExtractPath, arguments, Path.GetDirectoryName(sourcePath)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode >= 2)
            {
                var details = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
                throw new InvalidOperationException(CoreText.Get(
                    "MetadataBackup_ExtractionFailed",
                    "attachments",
                    result.ExitCode,
                    details.Trim()));
            }

            foreach (var extractedFile in extractedFiles)
            {
                if (!File.Exists(extractedFile.ExtractionPath))
                {
                    throw new InvalidOperationException(CoreText.Get(
                        "MetadataBackup_AttachmentMissing",
                        Path.GetFileName(extractedFile.ExtractionPath)));
                }
            }

            Directory.CreateDirectory(destinationDirectory);
            var destinations = new List<string>(extractedFiles.Count);
            foreach (var extractedFile in extractedFiles)
            {
                var destination = GetAvailableFilePath(
                    destinationDirectory,
                    extractedFile.DestinationName);
                File.Move(extractedFile.ExtractionPath, destination, overwrite: false);
                destinations.Add(destination);
            }
            return destinations;
        }
        finally
        {
            TryDeleteDirectory(extractionDirectory);
        }
    }

    private static FileInfo ValidateSource(
        string sourcePath,
        string mkvMergePath,
        MkvIdentification identification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mkvMergePath);
        ArgumentNullException.ThrowIfNull(identification);
        cancellationToken.ThrowIfCancellationRequested();

        var source = new FileInfo(sourcePath);
        if (!source.Exists)
        {
            throw new FileNotFoundException(CoreText.Get("MetadataBackup_SourceMissing"), sourcePath);
        }
        return source;
    }

    private static string CreateVideoBackupDirectory(FileInfo source)
    {
        var root = Path.Combine(
            source.DirectoryName
            ?? throw new InvalidOperationException(CoreText.Get("MetadataBackup_SourceDirectoryMissing")),
            BackupDirectoryName);
        Directory.CreateDirectory(root);
        var videoDirectory = Path.Combine(root, source.Name);
        Directory.CreateDirectory(videoDirectory);
        return videoDirectory;
    }

    private static bool IsMatroska(string? containerType) =>
        containerType?.Contains("Matroska", StringComparison.OrdinalIgnoreCase) == true
        || containerType?.Contains("WebM", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsTrackType(MkvTrackInfo track, string type) =>
        string.Equals(track.Type, type, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAudioLanguage(MkvTrackInfo track, AudioTrackLanguage language)
    {
        var (legacyCode, ietfCode) = language switch
        {
            AudioTrackLanguage.English => ("eng", "en"),
            AudioTrackLanguage.Japanese => ("jpn", "ja"),
            AudioTrackLanguage.Korean => ("kor", "ko"),
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
        };

        if (!IsUndeterminedLanguage(track.LanguageIetf))
        {
            return MatchesLanguageCode(track.LanguageIetf, legacyCode, ietfCode);
        }
        return MatchesLanguageCode(track.Language, legacyCode, ietfCode);
    }

    private static bool MatchesLanguageCode(string? value, string legacyCode, string ietfCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return string.Equals(normalized, legacyCode, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, ietfCode, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith($"{ietfCode}-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUndeterminedLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), "und", StringComparison.OrdinalIgnoreCase);

    private static string ResolveMkvExtractPath(string mkvMergePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(mkvMergePath))
                        ?? Environment.CurrentDirectory;
        return Path.Combine(directory, OperatingSystem.IsWindows() ? "mkvextract.exe" : "mkvextract");
    }

    private static void EnsureMkvExtractExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(CoreText.Get("MetadataBackup_MkvExtractMissing"), path);
        }
    }

    private static string MakeSafeAttachmentName(string? fileName, int id)
    {
        var candidate = string.IsNullOrWhiteSpace(fileName)
            ? $"attachment-{id}"
            : Path.GetFileName(fileName.Trim());
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalidCharacter, '_');
        }

        candidate = candidate.Trim().TrimEnd('.', ' ');
        if (candidate.Length == 0)
        {
            return $"attachment-{id}";
        }

        if (OperatingSystem.IsWindows()
            && ReservedWindowsFileNames.Contains(Path.GetFileNameWithoutExtension(candidate)))
        {
            candidate = $"_{candidate}";
        }
        return candidate;
    }

    private static string GetAvailableFilePath(
        string directory,
        string fileName,
        ISet<string>? reservedNames = null)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 0; suffix < int.MaxValue; suffix++)
        {
            var candidateName = suffix == 0
                ? fileName
                : $"{nameWithoutExtension} ({suffix}){extension}";
            var candidatePath = Path.Combine(directory, candidateName);
            if (!File.Exists(candidatePath)
                && (reservedNames is null || !reservedNames.Contains(candidateName)))
            {
                return candidatePath;
            }
        }

        throw new IOException(CoreText.Get("MetadataBackup_NoAvailableName", fileName));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for a failed or cancelled atomic write.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for a failed or cancelled atomic write.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for a failed or cancelled extraction.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for a failed or cancelled extraction.
        }
    }
}
