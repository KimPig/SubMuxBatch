using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Tests;

public sealed class QueueColumnSettingsTests
{
    [Fact]
    public void DefaultsShowAllQueueColumns()
    {
        var defaults = new AppSettings();
        var legacyJson = AppSettings.Deserialize("{}");

        AssertAllColumnsVisible(defaults);
        AssertAllColumnsVisible(legacyJson);
    }

    [Fact]
    public void RestoresFileColumnWhenPersistedSettingsHideEveryColumn()
    {
        var settings = AppSettings.Deserialize("""
            {
              "ShowFileColumn": false,
              "ShowCompositionColumn": false,
              "ShowMediaFormatColumn": false,
              "ShowDurationColumn": false,
              "ShowVideoCodecColumn": false,
              "ShowWorkColumn": false,
              "ShowStatusColumn": false
            }
            """);

        Assert.True(settings.ShowFileColumn);
        Assert.False(settings.ShowCompositionColumn);
        Assert.False(settings.ShowMediaFormatColumn);
        Assert.False(settings.ShowDurationColumn);
        Assert.False(settings.ShowVideoCodecColumn);
        Assert.False(settings.ShowWorkColumn);
        Assert.False(settings.ShowStatusColumn);
    }

    [Fact]
    public void PreservesExistingFormatAndCodecSettingKeysForMigration()
    {
        var legacySettings = AppSettings.Deserialize("""
            {
              "ShowMediaFormatColumn": false,
              "ShowVideoCodecColumn": false
            }
            """);

        Assert.True(legacySettings.ShowFileColumn);
        Assert.True(legacySettings.ShowCompositionColumn);
        Assert.False(legacySettings.ShowMediaFormatColumn);
        Assert.False(legacySettings.ShowVideoCodecColumn);
        Assert.True(legacySettings.ShowWorkColumn);
        Assert.True(legacySettings.ShowStatusColumn);
    }

    [Fact]
    public void CopyPreservesAllQueueColumnOptions()
    {
        var copy = new AppSettings
        {
            ShowFileColumn = false,
            ShowCompositionColumn = true,
            ShowMediaFormatColumn = false,
            ShowDurationColumn = true,
            ShowVideoCodecColumn = true,
            ShowWorkColumn = false,
            ShowStatusColumn = true
        }.Copy();

        Assert.False(copy.ShowFileColumn);
        Assert.True(copy.ShowCompositionColumn);
        Assert.False(copy.ShowMediaFormatColumn);
        Assert.True(copy.ShowDurationColumn);
        Assert.True(copy.ShowVideoCodecColumn);
        Assert.False(copy.ShowWorkColumn);
        Assert.True(copy.ShowStatusColumn);
    }

    [Fact]
    public void QueueColumnWeightsUseDefaultsAndAreNormalizedWhenInvalid()
    {
        var defaults = new AppSettings();
        var restored = AppSettings.Deserialize("""
            {
              "FileColumnWeight": 0,
              "CompositionColumnWeight": -1,
              "MediaFormatColumnWeight": 2.5,
              "DurationColumnWeight": 0,
              "VideoCodecColumnWeight": 0,
              "WorkColumnWeight": 0,
              "StatusColumnWeight": 0
            }
            """);

        Assert.Equal(AppSettings.DefaultFileColumnWeight, defaults.FileColumnWeight);
        Assert.Equal(AppSettings.DefaultCompositionColumnWeight, restored.CompositionColumnWeight);
        Assert.Equal(AppSettings.DefaultMediaFormatColumnWeight, defaults.MediaFormatColumnWeight);
        Assert.Equal(2.5, restored.MediaFormatColumnWeight);
        Assert.Equal(AppSettings.DefaultDurationColumnWeight, restored.DurationColumnWeight);
        Assert.Equal(AppSettings.DefaultVideoCodecColumnWeight, restored.VideoCodecColumnWeight);
        Assert.Equal(AppSettings.DefaultWorkColumnWeight, restored.WorkColumnWeight);
        Assert.Equal(AppSettings.DefaultStatusColumnWeight, restored.StatusColumnWeight);
    }

    [Fact]
    public void CopyPreservesQueueColumnWeights()
    {
        var copy = new AppSettings
        {
            FileColumnWeight = 3,
            CompositionColumnWeight = 1.2,
            MediaFormatColumnWeight = 0.6,
            DurationColumnWeight = 0.8,
            VideoCodecColumnWeight = 1.5,
            WorkColumnWeight = 2.7,
            StatusColumnWeight = 0.9
        }.Copy();

        Assert.Equal(3, copy.FileColumnWeight);
        Assert.Equal(1.2, copy.CompositionColumnWeight);
        Assert.Equal(0.6, copy.MediaFormatColumnWeight);
        Assert.Equal(0.8, copy.DurationColumnWeight);
        Assert.Equal(1.5, copy.VideoCodecColumnWeight);
        Assert.Equal(2.7, copy.WorkColumnWeight);
        Assert.Equal(0.9, copy.StatusColumnWeight);
    }
    private static void AssertAllColumnsVisible(AppSettings settings)
    {
        Assert.True(settings.ShowFileColumn);
        Assert.True(settings.ShowCompositionColumn);
        Assert.True(settings.ShowMediaFormatColumn);
        Assert.True(settings.ShowDurationColumn);
        Assert.True(settings.ShowVideoCodecColumn);
        Assert.True(settings.ShowWorkColumn);
        Assert.True(settings.ShowStatusColumn);
    }
}
