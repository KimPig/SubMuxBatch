using System.Text;
using SubMuxBatch.Core.Dependencies;

namespace SubMuxBatch.Core.Tests;

public sealed class BundledMkvToolNixProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SubMuxBatch-BundledMkvToolNixTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExtractsAllRequiredFilesAndRepairsCorruptedCache()
    {
        var resources = CreateResources();
        var provider = new BundledMkvToolNixProvider(
            _root,
            name => resources.TryGetValue(name, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

        var mkvMergePath = await provider.EnsureAvailableAsync();

        Assert.Equal(Path.Combine(_root, "mkvmerge.exe"), mkvMergePath, ignoreCase: true);
        Assert.Equal("mkvmerge", await File.ReadAllTextAsync(mkvMergePath));
        Assert.Equal("mkvextract", await File.ReadAllTextAsync(Path.Combine(_root, "mkvextract.exe")));
        Assert.Equal("ko", await File.ReadAllTextAsync(Path.Combine(_root, "locale", "ko", "LC_MESSAGES", "mkvtoolnix.mo")));
        Assert.Equal("license", await File.ReadAllTextAsync(Path.Combine(_root, "COPYING.txt")));
        Assert.Equal("readme", await File.ReadAllTextAsync(Path.Combine(_root, "README.txt")));
        Assert.Equal("pugixml", await File.ReadAllTextAsync(Path.Combine(_root, "licenses", "pugixml-MIT.txt")));

        await File.WriteAllTextAsync(mkvMergePath, "corrupted");
        await provider.EnsureAvailableAsync();

        Assert.Equal("mkvmerge", await File.ReadAllTextAsync(mkvMergePath));
    }

    [Fact]
    public void LicenseAndNoticeResourcesAreEmbedded()
    {
        var resourceNames = typeof(BundledMkvToolNixProvider).Assembly.GetManifestResourceNames();

        Assert.Contains("SubMuxBatch.Core.Resources.mkvtoolnix.COPYING.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.mkvtoolnix.README.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.mkvtoolnix.licenses.pugixml-MIT.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.mkvtoolnix.licenses.nlohmann-json-MIT.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.mkvtoolnix.licenses.QtWaitingSpinner-MIT.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.mkvtoolnix.licenses.LGPL-3.0.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.mkvtoolnix.licenses.LGPL-2.1.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.mkvtoolnix.licenses.CC-BY-3.0.txt", resourceNames);
        Assert.Contains("SubMuxBatch.Core.Resources.mkvtoolnix.licenses.Boost-1.0.txt", resourceNames);
    }

    [Fact]
    public async Task MissingResourceFailsWithoutLeavingPartialFile()
    {
        var resources = CreateResources();
        resources.Remove("SubMuxBatch.Core.Resources.mkvtoolnix.mkvextract.exe");
        var provider = new BundledMkvToolNixProvider(
            _root,
            name => resources.TryGetValue(name, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EnsureAvailableAsync());

        Assert.False(File.Exists(Path.Combine(_root, "mkvextract.exe")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Dictionary<string, byte[]> CreateResources() => new(StringComparer.Ordinal)
    {
        ["SubMuxBatch.Core.Resources.mkvtoolnix.mkvmerge.exe"] = Encoding.UTF8.GetBytes("mkvmerge"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.mkvextract.exe"] = Encoding.UTF8.GetBytes("mkvextract"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.locale.ko.LC_MESSAGES.mkvtoolnix.mo"] = Encoding.UTF8.GetBytes("ko"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.COPYING.txt"] = Encoding.UTF8.GetBytes("license"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.README.txt"] = Encoding.UTF8.GetBytes("readme"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.licenses.pugixml-MIT.txt"] = Encoding.UTF8.GetBytes("pugixml"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.licenses.nlohmann-json-MIT.txt"] = Encoding.UTF8.GetBytes("json"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.licenses.QtWaitingSpinner-MIT.txt"] = Encoding.UTF8.GetBytes("spinner"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.licenses.LGPL-3.0.txt"] = Encoding.UTF8.GetBytes("lgpl3"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.licenses.LGPL-2.1.txt"] = Encoding.UTF8.GetBytes("lgpl21"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.licenses.CC-BY-3.0.txt"] = Encoding.UTF8.GetBytes("ccby"),
        ["SubMuxBatch.Core.Resources.mkvtoolnix.licenses.Boost-1.0.txt"] = Encoding.UTF8.GetBytes("boost")
    };
}
