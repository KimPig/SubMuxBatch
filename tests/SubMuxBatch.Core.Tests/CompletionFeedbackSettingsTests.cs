using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Tests;

public sealed class CompletionFeedbackSettingsTests
{
    [Fact]
    public void DefaultsEnableNotificationWithoutSound()
    {
        var defaults = new AppSettings();
        var legacyJson = AppSettings.Deserialize("{}");

        Assert.True(defaults.ShowCompletionNotification);
        Assert.False(defaults.PlayCompletionSound);
        Assert.True(legacyJson.ShowCompletionNotification);
        Assert.False(legacyJson.PlayCompletionSound);
    }

    [Fact]
    public void DeserializesIndependentCompletionFeedbackOptions()
    {
        var settings = AppSettings.Deserialize("""
            {
              "ShowCompletionNotification": false,
              "PlayCompletionSound": true
            }
            """);

        Assert.False(settings.ShowCompletionNotification);
        Assert.True(settings.PlayCompletionSound);
    }

    [Fact]
    public void CopyPreservesIndependentCompletionFeedbackOptions()
    {
        var copy = new AppSettings
        {
            ShowCompletionNotification = false,
            PlayCompletionSound = true
        }.Copy();

        Assert.False(copy.ShowCompletionNotification);
        Assert.True(copy.PlayCompletionSound);
    }
}
