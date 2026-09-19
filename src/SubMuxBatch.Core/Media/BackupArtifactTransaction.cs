namespace SubMuxBatch.Core.Media;

internal sealed class BackupArtifactTransaction
{
    private readonly string _rootDirectory;
    private readonly string _videoDirectory;
    private readonly string _attachmentDirectory;
    private readonly List<string> _createdFiles = [];
    private readonly HashSet<string> _createdFileSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _createdDirectories = [];
    private readonly HashSet<string> _createdDirectorySet = new(StringComparer.OrdinalIgnoreCase);
    private bool _committed;

    public BackupArtifactTransaction(string sourcePath)
    {
        var source = new FileInfo(sourcePath);
        var sourceDirectory = source.DirectoryName
                              ?? throw new InvalidOperationException("The source directory could not be determined.");
        _rootDirectory = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            MetadataBackupService.BackupDirectoryName));
        _videoDirectory = Path.GetFullPath(Path.Combine(_rootDirectory, source.Name));
        _attachmentDirectory = Path.GetFullPath(Path.Combine(_videoDirectory, "attachments"));
    }

    public BackupArtifactCheckpoint Checkpoint => new(
        _createdFiles.Count,
        _createdDirectories.Count);

    public void RecordCreatedFile(string path)
    {
        if (_committed)
        {
            throw new InvalidOperationException("The backup transaction has already been committed.");
        }

        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.Equals(parent, _videoDirectory, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parent, _attachmentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to track an unexpected backup path: {fullPath}");
        }

        if (_createdFileSet.Add(fullPath))
        {
            _createdFiles.Add(fullPath);
        }
    }

    public void RecordCreatedDirectory(string path)
    {
        if (_committed)
        {
            throw new InvalidOperationException("The backup transaction has already been committed.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!IsExpectedDirectory(fullPath))
        {
            throw new InvalidOperationException($"Refusing to track an unexpected backup directory: {fullPath}");
        }

        if (_createdDirectorySet.Add(fullPath))
        {
            _createdDirectories.Add(fullPath);
        }
    }

    public IReadOnlyList<string> RollbackTo(BackupArtifactCheckpoint checkpoint)
    {
        if (_committed)
        {
            return [];
        }

        if (checkpoint.FileCount < 0
            || checkpoint.FileCount > _createdFiles.Count
            || checkpoint.DirectoryCount < 0
            || checkpoint.DirectoryCount > _createdDirectories.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        }

        var errors = new List<string>();
        for (var index = _createdFiles.Count - 1; index >= checkpoint.FileCount; index--)
        {
            var path = _createdFiles[index];
            try
            {
                EnsureSafeForDeletion(path);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                _createdFiles.RemoveAt(index);
                _createdFileSet.Remove(path);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidOperationException)
            {
                errors.Add($"{path}: {exception.Message}");
            }
        }

        for (var index = _createdDirectories.Count - 1;
             index >= checkpoint.DirectoryCount;
             index--)
        {
            var path = _createdDirectories[index];
            try
            {
                EnsureDirectoryIsNotReparsePoint(path);
                if (Directory.Exists(path)
                    && !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path, recursive: false);
                }

                if (!Directory.Exists(path))
                {
                    _createdDirectories.RemoveAt(index);
                    _createdDirectorySet.Remove(path);
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidOperationException)
            {
                errors.Add($"{path}: {exception.Message}");
            }
        }
        return errors;
    }

    public IReadOnlyList<string> Rollback() => RollbackTo(new BackupArtifactCheckpoint(0, 0));

    public void Commit()
    {
        _committed = true;
        _createdFiles.Clear();
        _createdFileSet.Clear();
        _createdDirectories.Clear();
        _createdDirectorySet.Clear();
    }

    private void EnsureSafeForDeletion(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.Equals(parent, _videoDirectory, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parent, _attachmentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The backup file is outside the expected backup directory.");
        }

        EnsureDirectoryIsNotReparsePoint(_rootDirectory);
        EnsureDirectoryIsNotReparsePoint(_videoDirectory);
        if (string.Equals(parent, _attachmentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            EnsureDirectoryIsNotReparsePoint(_attachmentDirectory);
        }

        if (File.Exists(fullPath)
            && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The backup file is a reparse point.");
        }
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        if (Directory.Exists(path)
            && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"The backup directory is a reparse point: {path}");
        }
    }

    private bool IsExpectedDirectory(string path) =>
        string.Equals(path, _rootDirectory, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, _videoDirectory, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, _attachmentDirectory, StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct BackupArtifactCheckpoint(int FileCount, int DirectoryCount);
