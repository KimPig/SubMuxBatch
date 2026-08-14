using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Tests;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"submux-batch-settings-{Guid.NewGuid():N}");

    [Fact]
    public void NewSettingsTakePriorityOverLegacySettings()
    {
        var current = Path.Combine(_root, "current", "settings.json");
        var legacy = Path.Combine(_root, "legacy", "settings.json");
        WriteSettings(current, "new_");
        WriteSettings(legacy, "legacy_");

        var loaded = AppSettings.LoadFromPaths(current, legacy);

        Assert.Equal("new_", loaded.OutputPrefix);
    }

    [Fact]
    public void LegacySettingsAreLoadedAndCopiedToTheNewLocation()
    {
        var current = Path.Combine(_root, "current", "settings.json");
        var legacy = Path.Combine(_root, "legacy", "settings.json");
        WriteSettings(legacy, "legacy_");

        var loaded = AppSettings.LoadFromPaths(current, legacy);

        Assert.Equal("legacy_", loaded.OutputPrefix);
        Assert.True(File.Exists(current));
        Assert.Equal("legacy_", AppSettings.Deserialize(File.ReadAllText(current)).OutputPrefix);
    }

    [Fact]
    public void MissingSettingsUseDefaults()
    {
        var loaded = AppSettings.LoadFromPaths(
            Path.Combine(_root, "current", "settings.json"),
            Path.Combine(_root, "legacy", "settings.json"));

        Assert.Equal("SubMux_", loaded.OutputPrefix);
    }

    [Fact]
    public void InvalidLegacySettingsUseDefaultsWithoutCreatingNewSettings()
    {
        var current = Path.Combine(_root, "current", "settings.json");
        var legacy = Path.Combine(_root, "legacy", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "{ invalid json }");

        var loaded = AppSettings.LoadFromPaths(current, legacy);

        Assert.Equal("SubMux_", loaded.OutputPrefix);
        Assert.False(File.Exists(current));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteSettings(string path, string outputPrefix)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "OutputPrefix": "{{outputPrefix}}"
            }
            """);
    }
}