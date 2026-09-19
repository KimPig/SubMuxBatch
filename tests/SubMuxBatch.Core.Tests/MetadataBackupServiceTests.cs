using System.Text.Json;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class MetadataBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SubMuxMetadataBackupTests-{Guid.NewGuid():N}");

    public MetadataBackupServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task WritesFullReportInsideOriginalFileNameFolderWithoutOverwriting()
    {
        var source = Path.Combine(_root, "sample.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var identification = new MkvIdentification(
            new MkvInspection([], [], 0, "QuickTime/MP4", FileSizeBytes: 4),
            "{\"container\":{\"type\":\"QuickTime/MP4\"},\"tracks\":[]}");
        var reader = new StaticRawReader(
        [
            new MediaInfoRawStream(
                "General",
                0,
                [new MediaInfoRawField("Title", "원본 제목")])
        ]);
        var service = new MetadataBackupService(new BackupRunner(), reader);

        var first = await service.BackupMetadataAsync(
            source,
            Path.Combine(_root, "mkvmerge.exe"),
            identification);
        var second = await service.BackupMetadataAsync(
            source,
            Path.Combine(_root, "mkvmerge.exe"),
            identification);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal("sample.mp4", Path.GetFileName(Path.GetDirectoryName(first)));
        Assert.Equal(
            MetadataBackupService.BackupDirectoryName,
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(first))));
        var backupRoot = Path.GetDirectoryName(Path.GetDirectoryName(first))!;
        Assert.False(File.GetAttributes(backupRoot).HasFlag(FileAttributes.Hidden));
        Assert.Equal("metadata.json", Path.GetFileName(first));
        Assert.Equal("metadata (1).json", Path.GetFileName(second));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(first));
        var root = document.RootElement;
        Assert.Equal(MetadataBackupService.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("sample.mp4", root.GetProperty("source").GetProperty("fileName").GetString());
        Assert.Equal("QuickTime/MP4", root.GetProperty("mkvMergeIdentification").GetProperty("container").GetProperty("type").GetString());
        Assert.Equal("원본 제목", root.GetProperty("mediaInfo")[0].GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("matroskaTagsXml").ValueKind);
    }

    [Fact]
    public async Task MatroskaMetadataBackupEmbedsRawTagsAndChaptersXml()
    {
        var (source, mkvMerge, _) = await CreateMatroskaToolFilesAsync("metadata.mkv");
        var identification = new MkvIdentification(
            new MkvInspection([], [], 1, "Matroska"),
            "{\"container\":{\"type\":\"Matroska\"},\"tracks\":[]}");
        var runner = new BackupRunner();
        var service = new MetadataBackupService(runner, new StaticRawReader([]));

        var backupPath = await service.BackupMetadataAsync(source, mkvMerge, identification);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(backupPath));
        Assert.Contains("ORIGINAL_TITLE", document.RootElement.GetProperty("matroskaTagsXml").GetString());
        Assert.Contains("ChapterAtom", document.RootElement.GetProperty("matroskaChaptersXml").GetString());
        Assert.Equal(["tags", "chapters"], runner.ExtractModes);
    }

    [Fact]
    public async Task BacksUpSubtitleTracksAndAttachmentsIndependentlyWithoutOverwriting()
    {
        var (source, mkvMerge, _) = await CreateMatroskaToolFilesAsync("tracks.mkv");
        var identification = new MkvIdentification(
            new MkvInspection(
                [new MkvTrackInfo("subtitles", "S_TEXT/ASS", false, false, "kor", "ko", "Original", 2)],
                [
                    new MkvAttachmentInfo("font.ttf", "font/ttf", null, 3, "1", 4),
                    new MkvAttachmentInfo("../font.ttf", "font/ttf", null, 3, "2", 5)
                ],
                0,
                "Matroska"),
            "{\"container\":{\"type\":\"Matroska\"},\"tracks\":[]}");
        var runner = new BackupRunner();
        var attachmentTemporaryRoot = Path.Combine(_root, "short-attachment-temp");
        var service = new MetadataBackupService(
            runner,
            new StaticRawReader([]),
            attachmentTemporaryRoot);

        await service.BackupSubtitlesAsync(source, mkvMerge, identification);
        await service.BackupAttachmentsAsync(source, mkvMerge, identification);
        await service.BackupSubtitlesAsync(source, mkvMerge, identification);
        await service.BackupAttachmentsAsync(source, mkvMerge, identification);

        var videoDirectory = Path.Combine(
            _root,
            MetadataBackupService.BackupDirectoryName,
            "tracks.mkv");
        Assert.True(File.Exists(Path.Combine(videoDirectory, "subtitles.mks")));
        Assert.True(File.Exists(Path.Combine(videoDirectory, "subtitles (1).mks")));
        var attachmentDirectory = Path.Combine(videoDirectory, "attachments");
        Assert.Equal(
            ["font (1).ttf", "font (2).ttf", "font (3).ttf", "font.ttf"],
            Directory.GetFiles(attachmentDirectory)
                .Select(static path => Path.GetFileName(path)!)
                .Order()
                .ToArray());
        Assert.Equal(2, runner.ExtractModes.Count(static mode => mode == "attachments"));
        Assert.All(runner.AttachmentOutputPaths, path =>
        {
            Assert.StartsWith(
                Path.GetFullPath(attachmentTemporaryRoot) + Path.DirectorySeparatorChar,
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("attachment-", Path.GetFileName(path), StringComparison.Ordinal);
            Assert.EndsWith(".bin", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(MetadataBackupService.BackupDirectoryName, path, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(Directory.EnumerateDirectories(attachmentTemporaryRoot));
        Assert.All(
            runner.MuxCalls,
            static arguments => Assert.Contains("--subtitle-tracks", arguments));
    }

    [Fact]
    public async Task SubtitleAndAttachmentBackupsDoNotCreateFolderWhenThereIsNothingToSave()
    {
        var source = Path.Combine(_root, "empty.avi");
        await File.WriteAllBytesAsync(source, [1]);
        var identification = new MkvIdentification(
            new MkvInspection([], [], 0, "AVI"),
            "{\"container\":{\"type\":\"AVI\"},\"tracks\":[]}");

        var service = new MetadataBackupService(new BackupRunner(), new StaticRawReader([]));
        var subtitlePaths = await service.BackupSubtitlesAsync(
            source,
            Path.Combine(_root, "mkvmerge.exe"),
            identification);
        var attachmentPaths = await service.BackupAttachmentsAsync(
            source,
            Path.Combine(_root, "mkvmerge.exe"),
            identification);

        Assert.Empty(subtitlePaths);
        Assert.Empty(attachmentPaths);
        Assert.False(Directory.Exists(Path.Combine(
            _root,
            MetadataBackupService.BackupDirectoryName,
            "empty.avi")));
    }

    [Fact]
    public async Task LongBackupDestinationUsesShortLocalAttachmentExtractionPath()
    {
        var longSourceDirectory = Path.Combine(
            _root,
            new string('a', 60),
            new string('b', 60),
            new string('c', 60));
        Directory.CreateDirectory(longSourceDirectory);
        var source = Path.Combine(longSourceDirectory, $"{new string('v', 80)}.mkv");
        var mkvMerge = Path.Combine(_root, "mkvmerge.exe");
        var mkvExtract = Path.Combine(_root, "mkvextract.exe");
        await File.WriteAllBytesAsync(source, [1]);
        await File.WriteAllBytesAsync(mkvMerge, [1]);
        await File.WriteAllBytesAsync(mkvExtract, [1]);
        var identification = new MkvIdentification(
            new MkvInspection(
                [],
                [new MkvAttachmentInfo("FOT-MatisseVPro-UB.otf", "font/otf", null, 3, "1", 4)],
                0,
                "Matroska"),
            "{\"container\":{\"type\":\"Matroska\"},\"tracks\":[]}");
        var runner = new BackupRunner();
        var attachmentTemporaryRoot = Path.Combine(_root, "short-temp");
        var service = new MetadataBackupService(
            runner,
            new StaticRawReader([]),
            attachmentTemporaryRoot);

        var paths = await service.BackupAttachmentsAsync(source, mkvMerge, identification);

        var backup = Assert.Single(paths);
        Assert.True(backup.Length > 260);
        Assert.True(File.Exists(backup));
        var extractionPath = Assert.Single(runner.AttachmentOutputPaths);
        Assert.StartsWith(
            Path.GetFullPath(attachmentTemporaryRoot) + Path.DirectorySeparatorChar,
            Path.GetFullPath(extractionPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(extractionPath.Length < 260);
        Assert.DoesNotContain(Path.GetFileName(source), extractionPath, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(attachmentTemporaryRoot));
    }

    [Fact]
    public async Task BacksUpOnlyAudioTracksExcludedByLanguageFilter()
    {
        var source = Path.Combine(_root, "audio.ts");
        var mkvMerge = Path.Combine(_root, "mkvmerge.exe");
        await File.WriteAllBytesAsync(source, [1]);
        await File.WriteAllBytesAsync(mkvMerge, [1]);
        var identification = new MkvIdentification(
            new MkvInspection(
                [
                    new MkvTrackInfo("audio", "A_AAC", true, false, "jpn", "ja", "Japanese", 0),
                    new MkvTrackInfo("audio", "A_AAC", false, false, "eng", "en", "English", 1),
                    new MkvTrackInfo("audio", "A_AAC", false, false, "kor", "ko", "Korean", 2)
                ],
                [],
                0,
                "MPEG-TS"),
            "{\"container\":{\"type\":\"MPEG-TS\"},\"tracks\":[]}");
        var runner = new BackupRunner();

        var path = await new MetadataBackupService(runner, new StaticRawReader([]))
            .BackupExcludedAudioTracksAsync(
                source,
                mkvMerge,
                identification,
                AudioTrackLanguage.Japanese);

        Assert.NotNull(path);
        Assert.Equal("excluded-audio.mka", Path.GetFileName(path));
        var arguments = Assert.Single(runner.MuxCalls);
        var selector = arguments.ToList().IndexOf("--audio-tracks");
        Assert.Equal("1,2", arguments[selector + 1]);
        Assert.DoesNotContain("0", arguments[selector + 1].Split(','));
    }

    [Fact]
    public async Task DoesNotBackUpAudioWhenFilterWouldNotRemoveAnyTrack()
    {
        var source = Path.Combine(_root, "single.mkv");
        await File.WriteAllBytesAsync(source, [1]);
        var identification = new MkvIdentification(
            new MkvInspection(
                [new MkvTrackInfo("audio", "A_AAC", true, false, null, null, null, 0)],
                [],
                0,
                "Matroska"),
            "{\"container\":{\"type\":\"Matroska\"},\"tracks\":[]}");

        var path = await new MetadataBackupService(new BackupRunner(), new StaticRawReader([]))
            .BackupExcludedAudioTracksAsync(
                source,
                Path.Combine(_root, "mkvmerge.exe"),
                identification,
                AudioTrackLanguage.Japanese);

        Assert.Null(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<(string Source, string MkvMerge, string MkvExtract)> CreateMatroskaToolFilesAsync(
        string sourceName)
    {
        var source = Path.Combine(_root, sourceName);
        var mkvMerge = Path.Combine(_root, "mkvmerge.exe");
        var mkvExtract = Path.Combine(_root, "mkvextract.exe");
        await File.WriteAllBytesAsync(source, [1]);
        await File.WriteAllBytesAsync(mkvMerge, [1]);
        await File.WriteAllBytesAsync(mkvExtract, [1]);
        return (source, mkvMerge, mkvExtract);
    }

    private sealed class StaticRawReader(IReadOnlyList<MediaInfoRawStream> streams) : IMediaInfoRawReader
    {
        public Task<IReadOnlyList<MediaInfoRawStream>> ReadRawReportAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(streams);
    }

    private sealed class BackupRunner : IProcessRunner
    {
        public List<string> ExtractModes { get; } = [];
        public List<string> AttachmentOutputPaths { get; } = [];
        public List<IReadOnlyList<string>> MuxCalls { get; } = [];

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            if (request.FileName.Contains("mkvextract", StringComparison.OrdinalIgnoreCase))
            {
                var mode = request.Arguments[1];
                ExtractModes.Add(mode);
                if (mode == "attachments")
                {
                    foreach (var mapping in request.Arguments.Skip(2))
                    {
                        var separator = mapping.IndexOf(':');
                        var outputPath = mapping[(separator + 1)..];
                        AttachmentOutputPaths.Add(outputPath);
                        File.WriteAllText(outputPath, mapping[..separator]);
                    }
                    return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
                }

                var output = mode == "tags"
                    ? "﻿<Tags><Tag><Simple><Name>ORIGINAL_TITLE</Name><String>Test</String></Simple></Tag></Tags>"
                    : "<Chapters><EditionEntry><ChapterAtom /></EditionEntry></Chapters>";
                return Task.FromResult(new ProcessResult(0, output, string.Empty));
            }

            MuxCalls.Add(request.Arguments);
            var outputIndex = request.Arguments.ToList().IndexOf("-o") + 1;
            File.WriteAllBytes(request.Arguments[outputIndex], [1, 2, 3]);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
