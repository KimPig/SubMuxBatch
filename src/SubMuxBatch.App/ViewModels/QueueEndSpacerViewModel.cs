namespace SubMuxBatch.App.ViewModels;

/// <summary>
/// Scrollable queue tail used only to provide a blank click target after the last job.
/// </summary>
public sealed class QueueEndSpacerViewModel
{
    public bool IsQueueEndSpacer => true;
}
