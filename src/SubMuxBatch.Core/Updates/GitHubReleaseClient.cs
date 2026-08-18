using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Updates;

public sealed class GitHubReleaseClient
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/KimPig/SubMuxBatch/releases/latest";
    private const long MaximumArchiveBytes = 1L * 1024 * 1024 * 1024;
    private const long MaximumExtractedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 1024;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;
    private readonly string _updatesDirectory;

    public GitHubReleaseClient(HttpClient? httpClient = null, string? updatesDirectory = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _updatesDirectory = updatesDirectory ?? UpdateStorage.UpdatesDirectory;
    }

    public async Task<UpdateRelease?> GetNewerReleaseAsync(
        UpdateVersion currentVersion,
        string runtime,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var requestCancellation = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        AddGitHubHeaders(request);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestCancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(requestCancellation).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
            stream,
            cancellationToken: requestCancellation).ConfigureAwait(false)
                      ?? throw new InvalidDataException("GitHub returned an empty release response.");
        if (release.Draft || release.Prerelease || !UpdateVersion.TryParse(release.TagName, out var latestVersion))
        {
            return null;
        }

        if (latestVersion <= currentVersion)
        {
            return null;
        }

        var expectedAssetName = $"SubMuxBatch-v{latestVersion}-{runtime}.zip";
        var asset = release.Assets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, expectedAssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            throw new InvalidDataException($"The release does not contain '{expectedAssetName}'.");
        }

        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl)
            || downloadUrl.Scheme != Uri.UriSchemeHttps
            || !downloadUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release asset has an invalid download URL.");
        }

        if (asset.Size is <= 0 or > MaximumArchiveBytes)
        {
            throw new InvalidDataException("The release asset has an invalid size.");
        }

        var digest = ParseSha256Digest(asset.Digest);
        return new UpdateRelease(
            latestVersion,
            release.TagName,
            release.HtmlUrl,
            asset.Name,
            downloadUrl,
            asset.Size,
            digest);
    }

    public async Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_updatesDirectory);
        var updateRoot = Path.Combine(
            _updatesDirectory,
            $"v{release.Version}-{Guid.NewGuid():N}");
        var packageDirectory = Path.Combine(updateRoot, "package");
        Directory.CreateDirectory(packageDirectory);
        var archivePath = Path.Combine(updateRoot, release.AssetName);

        try
        {
            await DownloadArchiveAsync(release, archivePath, progress, cancellationToken).ConfigureAwait(false);
            ExtractArchive(archivePath, packageDirectory);
            var packageExecutablePath = Path.Combine(packageDirectory, "SubMuxBatch.exe");
            if (!File.Exists(packageExecutablePath))
            {
                throw new InvalidDataException("The update package does not contain SubMuxBatch.exe.");
            }

            return new PreparedUpdate(
                release,
                updateRoot,
                packageDirectory,
                packageExecutablePath);
        }
        catch
        {
            TryDeleteDirectory(updateRoot);
            throw;
        }
    }

    private async Task DownloadArchiveAsync(
        UpdateRelease release,
        string archivePath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var downloadCancellation = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("SubMuxBatch-Updater");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            downloadCancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
        {
            throw new InvalidDataException("The downloaded update archive is too large.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(downloadCancellation).ConfigureAwait(false);
        long received = 0;
        {
            await using var destination = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, downloadCancellation).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;
                if (received > MaximumArchiveBytes)
                {
                    throw new InvalidDataException("The downloaded update archive is too large.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), downloadCancellation).ConfigureAwait(false);
                progress?.Report(new UpdateDownloadProgress(received, release.AssetSize));
            }

            await destination.FlushAsync(downloadCancellation).ConfigureAwait(false);
        }

        if (received != release.AssetSize)
        {
            throw new InvalidDataException(
                $"The update download is incomplete. Expected {release.AssetSize} bytes, received {received} bytes.");
        }

        if (release.Sha256Digest is not null)
        {
            await using var verifyStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(verifyStream, downloadCancellation).ConfigureAwait(false));
            if (!actual.Equals(release.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update archive SHA-256 digest does not match GitHub.");
            }
        }
    }

    internal static void ExtractArchive(string archivePath, string packageDirectory)
    {
        var packageRoot = Path.GetFullPath(packageDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        long extractedBytes = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("The update archive contains too many files.");
        }

        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(packageDirectory, entry.FullName));
            if (!destinationPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update archive contains an unsafe path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaximumExtractedBytes)
            {
                throw new InvalidDataException("The extracted update package is too large.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
                                      ?? throw new InvalidDataException("The update archive path is invalid."));
            entry.ExtractToFile(destinationPath, overwrite: false);
        }
    }

    private static string? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = digest[prefix.Length..].Trim();
        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToUpperInvariant()
            : null;
    }

    private static void AddGitHubHeaders(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("SubMuxBatch-Updater");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public GitHubReleaseAsset[] Assets { get; init; } = [];
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
