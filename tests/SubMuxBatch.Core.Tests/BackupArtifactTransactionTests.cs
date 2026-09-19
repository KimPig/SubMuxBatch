using SubMuxBatch.Core.Media;

namespace SubMuxBatch.Core.Tests;

public sealed class BackupArtifactTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SubMuxBackupTransactionTests-{Guid.NewGuid():N}");

    public BackupArtifactTransactionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RollbackDeletesOnlyTrackedFilesAndNewDirectories()
    {
        var source = Path.Combine(_root, "episode.mkv");
        File.WriteAllBytes(source, [1]);
        var backupRoot = Path.Combine(_root, MetadataBackupService.BackupDirectoryName);
        var otherVideoDirectory = Path.Combine(backupRoot, "other.mkv");
        Directory.CreateDirectory(otherVideoDirectory);
        var otherBackup = Path.Combine(otherVideoDirectory, "metadata.json");
        File.WriteAllText(otherBackup, "keep");

        var transaction = new BackupArtifactTransaction(source);
        var videoDirectory = Path.Combine(backupRoot, "episode.mkv");
        var attachmentDirectory = Path.Combine(videoDirectory, "attachments");
        Directory.CreateDirectory(attachmentDirectory);
        var metadata = Path.Combine(videoDirectory, "metadata.json");
        var font = Path.Combine(attachmentDirectory, "font.ttf");
        File.WriteAllText(metadata, "new");
        File.WriteAllText(font, "new");
        transaction.RecordCreatedDirectory(videoDirectory);
        transaction.RecordCreatedDirectory(attachmentDirectory);
        transaction.RecordCreatedFile(metadata);
        transaction.RecordCreatedFile(font);

        var errors = transaction.Rollback();

        Assert.Empty(errors);
        Assert.False(File.Exists(metadata));
        Assert.False(File.Exists(font));
        Assert.False(Directory.Exists(videoDirectory));
        Assert.True(File.Exists(otherBackup));
        Assert.True(Directory.Exists(backupRoot));
    }

    [Fact]
    public void RollbackRemovesNewBackupRootWhenItBecomesEmpty()
    {
        var source = Path.Combine(_root, "episode.mp4");
        File.WriteAllBytes(source, [1]);
        var transaction = new BackupArtifactTransaction(source);
        var backupRoot = Path.Combine(_root, MetadataBackupService.BackupDirectoryName);
        var videoDirectory = Path.Combine(backupRoot, "episode.mp4");
        Directory.CreateDirectory(videoDirectory);
        var metadata = Path.Combine(videoDirectory, "metadata.json");
        File.WriteAllText(metadata, "new");
        transaction.RecordCreatedDirectory(backupRoot);
        transaction.RecordCreatedDirectory(videoDirectory);
        transaction.RecordCreatedFile(metadata);

        var errors = transaction.Rollback();

        Assert.Empty(errors);
        Assert.False(Directory.Exists(videoDirectory));
        Assert.False(Directory.Exists(backupRoot));
    }

    [Fact]
    public void RollbackPreservesPreexistingAndUntrackedFiles()
    {
        var source = Path.Combine(_root, "episode.avi");
        File.WriteAllBytes(source, [1]);
        var videoDirectory = Path.Combine(
            _root,
            MetadataBackupService.BackupDirectoryName,
            "episode.avi");
        Directory.CreateDirectory(videoDirectory);
        var existing = Path.Combine(videoDirectory, "existing.json");
        File.WriteAllText(existing, "existing");
        var transaction = new BackupArtifactTransaction(source);
        var tracked = Path.Combine(videoDirectory, "metadata.json");
        var untracked = Path.Combine(videoDirectory, "user-note.txt");
        File.WriteAllText(tracked, "new");
        File.WriteAllText(untracked, "do not delete");
        transaction.RecordCreatedFile(tracked);

        var errors = transaction.Rollback();

        Assert.Empty(errors);
        Assert.False(File.Exists(tracked));
        Assert.Equal("existing", File.ReadAllText(existing));
        Assert.Equal("do not delete", File.ReadAllText(untracked));
        Assert.True(Directory.Exists(videoDirectory));
    }

    [Fact]
    public void RefusesToTrackFileOutsideExpectedVideoDirectory()
    {
        var source = Path.Combine(_root, "episode.webm");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllBytes(source, [1]);
        File.WriteAllText(outside, "keep");
        var transaction = new BackupArtifactTransaction(source);

        Assert.Throws<InvalidOperationException>(() => transaction.RecordCreatedFile(outside));
        Assert.Empty(transaction.Rollback());
        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public void CommitPreservesTrackedBackup()
    {
        var source = Path.Combine(_root, "episode.mov");
        File.WriteAllBytes(source, [1]);
        var transaction = new BackupArtifactTransaction(source);
        var videoDirectory = Path.Combine(
            _root,
            MetadataBackupService.BackupDirectoryName,
            "episode.mov");
        Directory.CreateDirectory(videoDirectory);
        var metadata = Path.Combine(videoDirectory, "metadata.json");
        File.WriteAllText(metadata, "keep");
        transaction.RecordCreatedFile(metadata);

        transaction.Commit();

        Assert.Empty(transaction.Rollback());
        Assert.Equal("keep", File.ReadAllText(metadata));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
