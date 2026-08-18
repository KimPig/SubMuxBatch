using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Updates;

namespace SubMuxBatch.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"submux-update-tests-{Guid.NewGuid():N}");

    public UpdateTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("2026.08.19", 2026, 8, 19)]
    [InlineData("v2026.08.20", 2026, 8, 20)]
    [InlineData("2026.08.21+build", 2026, 8, 21)]
    public void DateVersionsAreParsedAndCompared(string value, int year, int month, int day)
    {
        Assert.True(UpdateVersion.TryParse(value, out var version));
        Assert.Equal(new UpdateVersion(year, month, day), version);
        Assert.True(version >= new UpdateVersion(2026, 8, 19));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1.2.3")]
    [InlineData("2026.13.01")]
    [InlineData("not-a-version")]
    public void InvalidDateVersionsAreRejected(string value) =>
        Assert.False(UpdateVersion.TryParse(value, out _));

    [Fact]
    public async Task LatestGitHubReleaseSelectsExactRuntimeAssetAndDigest()
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        using var httpClient = new HttpClient(new StubHttpHandler(_ => JsonResponse($$"""
            {
              "tag_name": "v2026.08.20",
              "html_url": "https://github.com/KimPig/SubMuxBatch/releases/tag/v2026.08.20",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "SubMuxBatch-v2026.08.20-win-x64.zip",
                  "browser_download_url": "https://github.com/KimPig/SubMuxBatch/releases/download/v2026.08.20/package.zip",
                  "size": 1234,
                  "digest": "sha256:{{digest}}"
                },
                {
                  "name": "SubMuxBatch-v2026.08.20-win-arm64.zip",
                  "browser_download_url": "https://github.com/KimPig/SubMuxBatch/releases/download/v2026.08.20/arm.zip",
                  "size": 5678,
                  "digest": null
                }
              ]
            }
            """)));
        var client = new GitHubReleaseClient(httpClient, _root);

        var release = await client.GetNewerReleaseAsync(
            new UpdateVersion(2026, 8, 19),
            "win-x64");

        Assert.NotNull(release);
        Assert.Equal(new UpdateVersion(2026, 8, 20), release.Version);
        Assert.Equal("SubMuxBatch-v2026.08.20-win-x64.zip", release.AssetName);
        Assert.Equal(digest.ToUpperInvariant(), release.Sha256Digest);
    }

    [Fact]
    public async Task SameOrOlderGitHubReleaseIsIgnored()
    {
        using var httpClient = new HttpClient(new StubHttpHandler(_ => JsonResponse("""
            {
              "tag_name": "v2026.08.19",
              "html_url": "https://github.com/KimPig/SubMuxBatch/releases/tag/v2026.08.19",
              "draft": false,
              "prerelease": false,
              "assets": []
            }
            """)));
        var client = new GitHubReleaseClient(httpClient, _root);

        var release = await client.GetNewerReleaseAsync(
            new UpdateVersion(2026, 8, 19),
            "win-x64");

        Assert.Null(release);
    }

    [Fact]
    public async Task DownloadIsDigestVerifiedAndSafelyExtracted()
    {
        var archive = CreateArchive(
            ("SubMuxBatch.exe", new byte[] { 1, 2, 3, 4 }),
            ("README.md", Encoding.UTF8.GetBytes("readme")));
        var digest = Convert.ToHexString(SHA256.HashData(archive));
        using var httpClient = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive)
        }));
        var client = new GitHubReleaseClient(httpClient, _root);
        var release = new UpdateRelease(
            new UpdateVersion(2026, 8, 20),
            "v2026.08.20",
            "https://github.com/KimPig/SubMuxBatch/releases/tag/v2026.08.20",
            "SubMuxBatch-v2026.08.20-win-x64.zip",
            new Uri("https://github.com/KimPig/SubMuxBatch/releases/download/v2026.08.20/package.zip"),
            archive.Length,
            digest);

        var prepared = await client.DownloadAndPrepareAsync(release);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(prepared.PackageExecutablePath));
        Assert.Equal("readme", await File.ReadAllTextAsync(Path.Combine(prepared.PackageDirectory, "README.md")));
    }

    [Fact]
    public void ArchiveTraversalIsRejected()
    {
        var archivePath = Path.Combine(_root, "unsafe.zip");
        File.WriteAllBytes(archivePath, CreateArchive(("../outside.exe", new byte[] { 1 })));
        var packagePath = Path.Combine(_root, "unsafe-package");
        Directory.CreateDirectory(packagePath);

        Assert.Throws<InvalidDataException>(() =>
            GitHubReleaseClient.ExtractArchive(archivePath, packagePath));
        Assert.False(File.Exists(Path.Combine(_root, "outside.exe")));
    }

    [Fact]
    public void PackageInstallReplacesRenamedExecutableLastAndPreservesUnrelatedFiles()
    {
        var package = Path.Combine(_root, "package-install");
        var target = Path.Combine(_root, "portable");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(package, "SubMuxBatch.exe"), [9, 8, 7]);
        File.WriteAllText(Path.Combine(package, "README.md"), "new");
        var targetExecutable = Path.Combine(target, "My SubMux.exe");
        File.WriteAllBytes(targetExecutable, [1, 2, 3]);
        File.WriteAllText(Path.Combine(target, "README.md"), "old");
        File.WriteAllText(Path.Combine(target, "mkvmerge.exe"), "keep");

        SelfUpdateApplier.InstallPackageFiles(package, targetExecutable);

        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(targetExecutable));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "README.md")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "mkvmerge.exe")));
    }

    [Fact]
    public void UpdateCommandRoundTripsPathsWithSpaces()
    {
        var arguments = new[]
        {
            "--apply-update",
            "--wait-pid", "123",
            "--wait-start-utc-ticks", "456",
            "--package-directory", Path.Combine(_root, "package with spaces"),
            "--target-executable", Path.Combine(_root, "SubMux Batch.exe"),
            "--update-root", Path.Combine(_root, "update root")
        };

        Assert.True(SelfUpdateCommand.TryParse(arguments, out var command));
        Assert.NotNull(command);
        Assert.Equal(123, command.WaitProcessId);
        Assert.EndsWith("SubMux Batch.exe", command.TargetExecutablePath);
    }

    [Fact]
    public void AutomaticUpdateCheckDefaultsOnAndIsCopied()
    {
        var defaults = AppSettings.Deserialize("{}");
        Assert.True(defaults.CheckForUpdatesAutomatically);
        Assert.True(defaults.Copy().CheckForUpdatesAutomatically);

        defaults.CheckForUpdatesAutomatically = false;
        Assert.False(defaults.Copy().CheckForUpdatesAutomatically);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static byte[] CreateArchive(params (string Name, byte[] Content)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(file.Content);
            }
        }

        return output.ToArray();
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
