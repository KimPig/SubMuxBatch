using SubMuxBatch.Core.Domain;

namespace SubMuxBatch.Core.Discovery;

public sealed class MediaSetDiscovery(bool allowSubtitleSuffixMatch = false)
{

    public Task<IReadOnlyList<MediaSet>> DiscoverAsync(
        IEnumerable<string> inputs,
        bool includeSubdirectories,
        CancellationToken cancellationToken = default)
    {
        var inputSnapshot = inputs
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.Run<IReadOnlyList<MediaSet>>(
            () => Discover(inputSnapshot, includeSubdirectories, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<MediaSet> Discover(
        IReadOnlyList<string> inputs,
        bool includeSubdirectories,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, MediaSet>(StringComparer.OrdinalIgnoreCase);
        var suffixDirectoryCache = new Dictionary<string, SuffixDirectorySnapshot>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<MediaSet> discovered;
            if (File.Exists(input))
            {
                discovered = DiscoverFile(input, suffixDirectoryCache, cancellationToken);
            }
            else if (Directory.Exists(input))
            {
                var files = EnumerateSupportedFiles(input, includeSubdirectories, cancellationToken).ToArray();
                discovered = BuildMediaSets(files, cancellationToken);
            }
            else
            {
                continue;
            }

            foreach (var media in discovered)
            {
                if (results.TryGetValue(media.Key.Canonical, out var existing))
                {
                    results[media.Key.Canonical] = existing.Merge(media);
                }
                else
                {
                    results.Add(media.Key.Canonical, media);
                }
            }
        }

        return results.Values
            .OrderBy(static media => media.Key.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static media => media.Key.Stem, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<MediaSet> DiscoverFile(
        string input,
        IDictionary<string, SuffixDirectorySnapshot> suffixDirectoryCache,
        CancellationToken cancellationToken)
    {
        if (!MediaInputFormats.IsSupported(input))
        {
            return [];
        }

        var fullInputPath = Path.GetFullPath(input);
        if (!allowSubtitleSuffixMatch)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddExactFileAndCompanions(fullInputPath, files);
            return BuildExactMediaSets(files, cancellationToken);
        }

        var directory = Path.GetDirectoryName(fullInputPath)!;
        if (!suffixDirectoryCache.TryGetValue(directory, out var snapshot))
        {
            var directoryFiles = EnumerateSupportedFiles(directory, recursive: false, cancellationToken).ToArray();
            var mediaSets = BuildSuffixMediaSets(directoryFiles, cancellationToken);
            snapshot = new SuffixDirectorySnapshot(
                mediaSets.ToDictionary(static media => media.Key.Canonical, StringComparer.OrdinalIgnoreCase),
                mediaSets
                    .SelectMany(static media => media.CandidateVideoPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            suffixDirectoryCache.Add(directory, snapshot);
        }

        MediaKey targetKey;
        if (MediaInputFormats.IsVideo(fullInputPath))
        {
            targetKey = MediaKey.FromPath(fullInputPath);
        }
        else
        {
            var owner = FindBestVideoOwner(fullInputPath, snapshot.InputVideos);
            targetKey = owner is null ? MediaKey.FromPath(fullInputPath) : MediaKey.FromPath(owner);
        }

        return snapshot.MediaSetsByCanonical.TryGetValue(targetKey.Canonical, out var media)
            ? [media]
            : [];
    }

    private IReadOnlyList<MediaSet> BuildMediaSets(
        IEnumerable<string> files,
        CancellationToken cancellationToken) =>
        allowSubtitleSuffixMatch
            ? BuildSuffixMediaSets(files, cancellationToken)
            : BuildExactMediaSets(files, cancellationToken);

    private IReadOnlyList<MediaSet> BuildExactMediaSets(
        IEnumerable<string> files,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, MediaSetBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in NormalizeFiles(files))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = MediaKey.FromPath(file);
            GetOrCreateBuilder(groups, key).Add(file);
        }

        return BuildSorted(groups.Values);
    }

    private IReadOnlyList<MediaSet> BuildSuffixMediaSets(
        IEnumerable<string> files,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeFiles(files).ToArray();
        var videoFiles = GetInputVideos(normalized);
        var groups = new Dictionary<string, MediaSetBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var video in videoFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = MediaKey.FromPath(video);
            GetOrCreateBuilder(groups, key).Add(video);
        }

        foreach (var subtitle in normalized.Where(file => MediaInputFormats.IsSubtitle(file)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = FindBestVideoOwner(subtitle, videoFiles);
            if (owner is null)
            {
                var key = MediaKey.FromPath(subtitle);
                GetOrCreateBuilder(groups, key).Add(subtitle);
                continue;
            }

            var ownerKey = MediaKey.FromPath(owner);
            GetOrCreateBuilder(groups, ownerKey).AddSubtitleCandidate(subtitle);
        }

        return BuildSorted(groups.Values);
    }

    private string[] GetInputVideos(IEnumerable<string> files) => files
        .Where(MediaInputFormats.IsVideo)
        .OrderBy(static file => file, StringComparer.Ordinal)
        .ToArray();

    private static string? FindBestVideoOwner(string subtitlePath, IReadOnlyCollection<string> videoFiles)
    {
        var subtitleDirectory = Path.GetDirectoryName(subtitlePath)!;
        var subtitleStem = Path.GetFileNameWithoutExtension(subtitlePath);

        return videoFiles
            .Where(video => string.Equals(
                Path.GetDirectoryName(video),
                subtitleDirectory,
                StringComparison.OrdinalIgnoreCase))
            .Where(video =>
            {
                var videoStem = Path.GetFileNameWithoutExtension(video);
                return string.Equals(subtitleStem, videoStem, StringComparison.OrdinalIgnoreCase)
                       || subtitleStem.StartsWith(videoStem + ".", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(video => Path.GetFileNameWithoutExtension(video).Length)
            .ThenBy(static video => video, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static IEnumerable<string> NormalizeFiles(IEnumerable<string> files) => files
        .Select(Path.GetFullPath)
        .Where(file => MediaInputFormats.IsSupported(file))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(static file => file, StringComparer.Ordinal);

    private static MediaSetBuilder GetOrCreateBuilder(
        IDictionary<string, MediaSetBuilder> groups,
        MediaKey key)
    {
        if (!groups.TryGetValue(key.Canonical, out var builder))
        {
            builder = new MediaSetBuilder(key);
            groups.Add(key.Canonical, builder);
        }

        return builder;
    }

    private static IReadOnlyList<MediaSet> BuildSorted(IEnumerable<MediaSetBuilder> builders) => builders
        .Select(static builder => builder.Build())
        .OrderBy(static media => media.Key.DirectoryPath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static media => media.Key.Stem, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static void AddExactFileAndCompanions(string input, ISet<string> files)
    {
        files.Add(input);
        var directory = Path.GetDirectoryName(input)!;
        var stem = Path.GetFileNameWithoutExtension(input);
        foreach (var candidateExtension in MediaInputFormats.SupportedExtensions)
        {
            var candidate = Path.Combine(directory, stem + candidateExtension);
            if (File.Exists(candidate))
            {
                files.Add(Path.GetFullPath(candidate));
            }
        }
    }

    private static IEnumerable<string> EnumerateSupportedFiles(
        string root,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (MediaInputFormats.IsSupported(file))
                {
                    yield return Path.GetFullPath(file);
                }
            }

            if (!recursive)
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var child in directories)
            {
                try
                {
                    if (WorkspaceNaming.IsWorkspaceDirectory(child))
                    {
                        continue;
                    }

                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
                catch (IOException)
                {
                    // The folder disappeared while scanning. Skip it.
                }
                catch (UnauthorizedAccessException)
                {
                    // The folder cannot be inspected. Skip it.
                }
            }
        }
    }

    private sealed record SuffixDirectorySnapshot(
        IReadOnlyDictionary<string, MediaSet> MediaSetsByCanonical,
        IReadOnlyCollection<string> InputVideos);

    private sealed class MediaSetBuilder(MediaKey key)
    {
        private readonly SortedSet<string> _videoCandidates = new(StringComparer.OrdinalIgnoreCase);
        private string? _ass;
        private string? _srt;
        private string? _smi;
        private readonly List<string> _assCandidates = [];
        private readonly List<string> _srtCandidates = [];
        private readonly List<string> _smiCandidates = [];

        public void Add(string path)
        {
            if (MediaInputFormats.IsVideo(path))
            {
                _videoCandidates.Add(path);
                return;
            }

            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".ass": _ass = path; break;
                case ".srt": _srt = path; break;
                case ".smi": _smi = path; break;
            }
        }

        public void AddSubtitleCandidate(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".ass": _assCandidates.Add(path); break;
                case ".srt": _srtCandidates.Add(path); break;
                case ".smi": _smiCandidates.Add(path); break;
            }
        }

        public MediaSet Build()
        {
            var videoCandidates = _videoCandidates.ToArray();
            return new MediaSet(
                key,
                videoCandidates.Length == 1 ? videoCandidates[0] : null,
                ChooseSubtitle(_ass, _assCandidates),
                ChooseSubtitle(_srt, _srtCandidates),
                ChooseSubtitle(_smi, _smiCandidates),
                videoCandidates);
        }

        private string? ChooseSubtitle(string? existing, IReadOnlyCollection<string> candidates)
        {
            if (candidates.Count == 0)
            {
                return existing;
            }

            return candidates
                .Select(path => new
                {
                    Path = path,
                    Suffix = GetSubtitleSuffix(key.Stem, path)
                })
                .OrderBy(candidate => GetSuffixCategory(candidate.Suffix))
                .ThenBy(candidate => candidate.Suffix.Length)
                .ThenBy(candidate => candidate.Suffix, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
                .Select(static candidate => candidate.Path)
                .First();
        }

        private static string GetSubtitleSuffix(string videoStem, string subtitlePath)
        {
            var subtitleStem = Path.GetFileNameWithoutExtension(subtitlePath);
            return subtitleStem.Length == videoStem.Length
                ? string.Empty
                : subtitleStem[(videoStem.Length + 1)..];
        }

        private static int GetSuffixCategory(string suffix)
        {
            if (suffix.Length == 0)
            {
                return 0;
            }

            var hasKoreanToken = suffix
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(token => token.Equals("ko", StringComparison.OrdinalIgnoreCase)
                              || token.Equals("kor", StringComparison.OrdinalIgnoreCase));
            return hasKoreanToken ? 1 : 2;
        }
    }
}
