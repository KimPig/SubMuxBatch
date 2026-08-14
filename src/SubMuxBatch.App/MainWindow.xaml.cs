using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using SubMuxBatch.App.Services;
using SubMuxBatch.App.ViewModels;
using SubMuxBatch.Core.Configuration;
using SubMuxBatch.Core.Dependencies;
using SubMuxBatch.Core.Discovery;
using SubMuxBatch.Core.Domain;
using SubMuxBatch.Core.External;
using SubMuxBatch.Core.Processing;

namespace SubMuxBatch.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string QueueItemsDragFormat = "SubMuxBatch.QueueItems";
    private const double MinimumDragDistance = 6;
    private const double MinimumQueueColumnWidth = 72;
    private readonly DependencyLocator _dependencyLocator = new();
    private AppSettings _settings = new();
    private DependencyReport? _dependencies;
    private CancellationTokenSource? _processingCancellation;
    private CancellationTokenSource? _scanCancellation;
    private SessionLogger? _logger;
    private readonly StringBuilder _sessionLog = new();
    private LogWindow? _logWindow;
    private CompletionNotificationWindow? _completionNotificationWindow;
    private bool _isBusy;
    private bool _isScanning;
    private bool _closeWhenIdle;
    private Point _queueDragStart;
    private QueueItemViewModel? _queueDragItem;
    private QueueItemViewModel[] _queueSelectionBeforeMouseDown = [];
    private bool _queueDragItemWasSelected;
    private string? _queueSortProperty;
    private ListSortDirection? _queueSortDirection;
    private long _queueRevealVersion;
    private bool _queueColumnWidthsDirty;
    private static readonly string[] QueueColumnProperties =
    [
        nameof(QueueItemViewModel.Name),
        nameof(QueueItemViewModel.DetectedFiles),
        nameof(QueueItemViewModel.MediaFormatText),
        nameof(QueueItemViewModel.VideoCodecText),
        nameof(QueueItemViewModel.PlanDescription),
        nameof(QueueItemViewModel.StatusText)
    ];

    private sealed record QueueDragPayload(
        QueueItemViewModel[] Items,
        QueueItemViewModel PrimaryItem);

    private sealed record CompletionFeedback(
        int Succeeded,
        int Warnings,
        int Failed,
        int Skipped);

    private bool IsInteractionLocked => _isBusy || _isScanning;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = GetDisplayVersion();
        DataContext = this;
    }

    private static string GetDisplayVersion()
    {
        var version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return $"v{version ?? "unknown"}";
    }

    public ObservableCollection<QueueItemViewModel> Jobs { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public GridLength FileColumnWidth => CreateQueueColumnWidth(_settings.ShowFileColumn, _settings.FileColumnWeight);
    public GridLength CompositionColumnWidth => CreateQueueColumnWidth(_settings.ShowCompositionColumn, _settings.CompositionColumnWeight);
    public GridLength MediaFormatColumnWidth => CreateQueueColumnWidth(_settings.ShowMediaFormatColumn, _settings.MediaFormatColumnWeight);
    public GridLength VideoCodecColumnWidth => CreateQueueColumnWidth(_settings.ShowVideoCodecColumn, _settings.VideoCodecColumnWeight);
    public GridLength WorkColumnWidth => CreateQueueColumnWidth(_settings.ShowWorkColumn, _settings.WorkColumnWeight);
    public GridLength StatusColumnWidth => CreateQueueColumnWidth(_settings.ShowStatusColumn, _settings.StatusColumnWeight);

    public Visibility FileColumnVisibility => ToVisibility(_settings.ShowFileColumn);
    public Visibility CompositionColumnVisibility => ToVisibility(_settings.ShowCompositionColumn);
    public Visibility MediaFormatColumnVisibility => ToVisibility(_settings.ShowMediaFormatColumn);
    public Visibility VideoCodecColumnVisibility => ToVisibility(_settings.ShowVideoCodecColumn);
    public Visibility WorkColumnVisibility => ToVisibility(_settings.ShowWorkColumn);
    public Visibility StatusColumnVisibility => ToVisibility(_settings.ShowStatusColumn);
    public Visibility FileColumnResizeVisibility => ToVisibility(CanResizeQueueColumn(nameof(QueueItemViewModel.Name)));
    public Visibility CompositionColumnResizeVisibility => ToVisibility(CanResizeQueueColumn(nameof(QueueItemViewModel.DetectedFiles)));
    public Visibility MediaFormatColumnResizeVisibility => ToVisibility(CanResizeQueueColumn(nameof(QueueItemViewModel.MediaFormatText)));
    public Visibility VideoCodecColumnResizeVisibility => ToVisibility(CanResizeQueueColumn(nameof(QueueItemViewModel.VideoCodecText)));
    public Visibility WorkColumnResizeVisibility => ToVisibility(CanResizeQueueColumn(nameof(QueueItemViewModel.PlanDescription)));

    private static GridLength CreateQueueColumnWidth(bool visible, double starWeight) =>
        visible ? new GridLength(starWeight, GridUnitType.Star) : new GridLength(0);

    private static Visibility ToVisibility(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowPlacementHelper.FitToCurrentWorkingArea(this);
        _settings = AppSettings.Load();
        RefreshQueueColumnPresentation();
        RecursiveCheckBox.IsChecked = _settings.IncludeSubdirectories;
        _logger = new SessionLogger();
        AppendLog("SubMux Batch를 시작했습니다.");
        RefreshDependencies();
        UpdateControls();
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "영상 또는 자막 파일 추가",
            Filter = $"지원 파일|{MediaInputFormats.SupportedDialogPattern}|영상|{MediaInputFormats.VideoDialogPattern}|자막|{MediaInputFormats.SubtitleDialogPattern}|모든 파일|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await AddPathsAsync(dialog.FileNames);
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "영상과 자막이 있는 폴더 선택",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await AddPathsAsync(dialog.FolderNames);
        }
    }

    private async Task AddPathsAsync(IEnumerable<string> paths, bool replaceQueue = false)
    {
        if (IsInteractionLocked)
        {
            return;
        }

        var snapshot = paths.ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        _isScanning = true;
        SetOverallIndeterminate();
        UpdateControls();

        try
        {
            OverallStatusText.Text = "파일을 확인하고 있습니다…";
            var discovery = new MediaSetDiscovery(
                _settings.OutputPrefix,
                _settings.AllowSubtitleSuffixMatch);
            var discovered = await discovery.DiscoverAsync(
                snapshot,
                RecursiveCheckBox.IsChecked == true,
                scanCancellation.Token);

            if (replaceQueue)
            {
                Jobs.Clear();
                JobsList.SelectedItem = null;
            }

            foreach (var media in discovered)
            {
                var existing = Jobs.FirstOrDefault(job =>
                    string.Equals(job.Key, media.Key.Canonical, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    Jobs.Add(new QueueItemViewModel(media, _settings));
                }
                else
                {
                    existing.Merge(media, _settings);
                }
            }

            if (discovered.Count > 0)
            {
                ClearQueueSortIndicators();
            }

            var mediaInfoTargets = Jobs.Where(static job => job.NeedsMediaInspection).ToArray();
            if (mediaInfoTargets.Length > 0)
            {
                OverallStatusText.Text = $"영상 정보를 읽고 있습니다… (0/{mediaInfoTargets.Length})";
                await LoadMediaDetailsAsync(mediaInfoTargets, scanCancellation.Token);
            }

            AppendLog($"입력 {snapshot.Length}개에서 미디어 작업 {discovered.Count}개를 확인했습니다.");
            if (JobsList.SelectedItem is null && Jobs.Count > 0)
            {
                JobsList.SelectedIndex = 0;
            }

            OverallStatusText.Text = Jobs.Count == 0
                ? "지원하는 파일을 찾지 못했습니다."
                : $"{Jobs.Count}개 작업, {Jobs.Count(static job => job.IsValid)}개 처리 가능";
        }
        catch (OperationCanceledException)
        {
            AppendLog("파일 탐색을 취소했습니다.");
        }
        catch (Exception exception)
        {
            AppendLog($"파일 탐색 실패: {exception.Message}");
            MessageBox.Show(this, exception.Message, "파일 탐색 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                _scanCancellation = null;
            }

            scanCancellation.Dispose();
            _isScanning = false;
            ClearOverallProgress();
            UpdateControls();
            CloseWhenIdleIfRequested();
        }
    }

    private async Task LoadMediaDetailsAsync(
        IReadOnlyList<QueueItemViewModel> targets,
        CancellationToken cancellationToken)
    {
        var mkvMergePath = _dependencies?.MkvMerge.Path;
        if (string.IsNullOrWhiteSpace(mkvMergePath))
        {
            foreach (var target in targets)
            {
                target.SetMediaInspectionError("mkvmerge 경로를 설정하면 표시됩니다.");
            }

            return;
        }

        using var concurrency = new SemaphoreSlim(3);
        var client = new MkvMergeClient(mkvMergePath, new ExternalProcessRunner());
        var completed = 0;
        var failed = 0;
        var tasks = targets.Select(async target =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var videoPath = target.Media.VideoPath;
                if (videoPath is null)
                {
                    return;
                }

                var inspection = await client.InspectAsync(
                    videoPath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => target.SetMediaInspection(inspection));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failed);
                await Dispatcher.InvokeAsync(() => target.SetMediaInspectionError(exception.Message));
            }
            finally
            {
                concurrency.Release();
                var current = Interlocked.Increment(ref completed);
                await Dispatcher.InvokeAsync(() =>
                    OverallStatusText.Text = $"영상 정보를 읽고 있습니다… ({current}/{targets.Count})");
            }
        });

        await Task.WhenAll(tasks);
        if (failed > 0)
        {
            AppendLog($"영상 정보 {targets.Count}개 중 {failed}개를 읽지 못했습니다.");
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = JobsList.SelectedItems.Cast<QueueItemViewModel>().ToArray();
        foreach (var item in selected)
        {
            Jobs.Remove(item);
        }

        ClearQueueSortIndicators();
        ClearOverallProgress();
        OverallStatusText.Text = Jobs.Count == 0
            ? "파일을 추가해 주세요."
            : $"{Jobs.Count}개 작업, {Jobs.Count(static job => job.IsValid)}개 처리 가능";

        UpdateControls();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Jobs.Clear();
        ClearQueueSortIndicators();
        JobsList.SelectedItem = null;
        OverallStatusText.Text = "파일을 추가해 주세요.";
        ClearOverallProgress();
        UpdateControls();
    }

    private void QueueHeader_Click(object sender, RoutedEventArgs e)
    {
        if (IsInteractionLocked
            || sender is not Button { Tag: string propertyName }
            || string.IsNullOrWhiteSpace(propertyName))
        {
            return;
        }

        var direction = string.Equals(propertyName, _queueSortProperty, StringComparison.Ordinal)
            && _queueSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        var selectedItems = JobsList.SelectedItems.Cast<QueueItemViewModel>().ToArray();
        var currentItem = JobsList.SelectedItem as QueueItemViewModel;
        var ordered = SortJobs(propertyName, direction).ToArray();

        ApplyQueueOrder(ordered);

        ClearQueueSortIndicators();
        _queueSortProperty = propertyName;
        _queueSortDirection = direction;
        UpdateQueueSortIndicators();
        RestoreQueueSelection(selectedItems, currentItem);
    }

    private void QueueColumnResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (IsInteractionLocked || sender is not Thumb { Tag: string propertyName })
        {
            return;
        }

        var nextPropertyName = GetNextVisibleQueueColumn(propertyName);
        var currentIndex = Array.IndexOf(QueueColumnProperties, propertyName);
        var nextIndex = nextPropertyName is null ? -1 : Array.IndexOf(QueueColumnProperties, nextPropertyName);
        if (currentIndex < 0 || nextIndex < 0 || QueueHeaderGrid.ColumnDefinitions.Count <= nextIndex)
        {
            return;
        }

        var currentColumn = QueueHeaderGrid.ColumnDefinitions[currentIndex];
        var nextColumn = QueueHeaderGrid.ColumnDefinitions[nextIndex];
        var pairWidth = currentColumn.ActualWidth + nextColumn.ActualWidth;
        if (pairWidth <= MinimumQueueColumnWidth * 2)
        {
            return;
        }

        var delta = Math.Clamp(
            e.HorizontalChange,
            MinimumQueueColumnWidth - currentColumn.ActualWidth,
            nextColumn.ActualWidth - MinimumQueueColumnWidth);
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        var totalWeight = GetQueueColumnWeight(propertyName) + GetQueueColumnWeight(nextPropertyName!);
        if (totalWeight <= 0)
        {
            return;
        }

        SetQueueColumnWeight(propertyName, totalWeight * (currentColumn.ActualWidth + delta) / pairWidth);
        SetQueueColumnWeight(nextPropertyName!, totalWeight * (nextColumn.ActualWidth - delta) / pairWidth);
        _queueColumnWidthsDirty = true;
        RefreshQueueColumnWidths();
    }

    private void QueueColumnResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_queueColumnWidthsDirty)
        {
            return;
        }

        _queueColumnWidthsDirty = false;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppendLog($"Queue column width save failed: {ex.Message}");
        }
    }
    private void QueueColumnContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        foreach (var menuItem in contextMenu.Items.OfType<MenuItem>())
        {
            if (menuItem.Tag is string propertyName)
            {
                menuItem.IsChecked = IsQueueColumnVisible(propertyName);
            }
        }
    }

    private void QueueColumnMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string propertyName } menuItem)
        {
            return;
        }

        var previousValue = IsQueueColumnVisible(propertyName);
        if (previousValue && !menuItem.IsChecked && CountVisibleQueueColumns() <= 1)
        {
            menuItem.IsChecked = true;
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        if (IsInteractionLocked || !SetQueueColumnVisible(propertyName, menuItem.IsChecked))
        {
            menuItem.IsChecked = previousValue;
            return;
        }

        try
        {
            _settings.Save();
        }
        catch (Exception exception)
        {
            SetQueueColumnVisible(propertyName, previousValue);
            menuItem.IsChecked = previousValue;
            MessageBox.Show(
                this,
                $"큐 열 설정을 저장하지 못했습니다.{Environment.NewLine}{exception.Message}",
                "설정 저장 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        RefreshQueueColumnPresentation();
    }

    private IEnumerable<QueueItemViewModel> SortJobs(
        string propertyName,
        ListSortDirection direction)
    {
        Func<QueueItemViewModel, string> keySelector = propertyName switch
        {
            nameof(QueueItemViewModel.Name) => static job => job.Name,
            nameof(QueueItemViewModel.DetectedFiles) => static job => job.DetectedFiles,
            nameof(QueueItemViewModel.MediaFormatText) => static job => job.MediaFormatText,
            nameof(QueueItemViewModel.VideoCodecText) => static job => job.VideoCodecText,
            nameof(QueueItemViewModel.PlanDescription) => static job => job.PlanDescription,
            nameof(QueueItemViewModel.StatusText) => static job => job.StatusText,
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, null)
        };

        return direction == ListSortDirection.Ascending
            ? Jobs.OrderBy(keySelector, StringComparer.CurrentCultureIgnoreCase)
            : Jobs.OrderByDescending(keySelector, StringComparer.CurrentCultureIgnoreCase);
    }

    private void ApplyQueueOrder(IReadOnlyList<QueueItemViewModel> ordered)
    {
        for (var destinationIndex = 0; destinationIndex < ordered.Count; destinationIndex++)
        {
            var currentIndex = Jobs.IndexOf(ordered[destinationIndex]);
            if (currentIndex != destinationIndex)
            {
                Jobs.Move(currentIndex, destinationIndex);
            }
        }
    }

    private void JobsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetQueueDragCandidate();
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (source is null
            || FindVisualParent<ScrollBar>(source) is not null
            || FindVisualParent<ButtonBase>(source) is not null)
        {
            return;
        }

        var container = ItemsControl.ContainerFromElement(JobsList, source) as ListBoxItem;
        if (container is null)
        {
            JobsList.UnselectAll();
            return;
        }

        if (IsInteractionLocked || container.DataContext is not QueueItemViewModel item)
        {
            return;
        }

        _queueDragStart = e.GetPosition(JobsList);
        _queueDragItem = item;
        _queueDragItemWasSelected = container.IsSelected;
        _queueSelectionBeforeMouseDown = JobsList.SelectedItems
            .Cast<QueueItemViewModel>()
            .ToArray();
    }

    private void JobsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            ResetQueueDragCandidate();
        }
    }

    private void JobsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetQueueDragCandidate();
            return;
        }

        if (IsInteractionLocked || _queueDragItem is null)
        {
            return;
        }

        var position = e.GetPosition(JobsList);
        var horizontalThreshold = Math.Max(
            MinimumDragDistance,
            SystemParameters.MinimumHorizontalDragDistance);
        var verticalThreshold = Math.Max(
            MinimumDragDistance,
            SystemParameters.MinimumVerticalDragDistance);
        if (Math.Abs(position.X - _queueDragStart.X) < horizontalThreshold
            && Math.Abs(position.Y - _queueDragStart.Y) < verticalThreshold)
        {
            return;
        }

        var draggedItem = _queueDragItem;
        var selectedItems = _queueDragItemWasSelected
                            && _queueSelectionBeforeMouseDown.Contains(draggedItem)
            ? _queueSelectionBeforeMouseDown
            : JobsList.SelectedItems.Cast<QueueItemViewModel>().ToArray();
        if (!selectedItems.Contains(draggedItem))
        {
            selectedItems = [draggedItem];
        }

        var selectedSet = selectedItems.ToHashSet();
        var draggedItems = Jobs.Where(selectedSet.Contains).ToArray();
        if (draggedItems.Length == 0)
        {
            ResetQueueDragCandidate();
            return;
        }

        var payload = new QueueDragPayload(draggedItems, draggedItem);
        RestoreQueueSelection(draggedItems, draggedItem);
        ResetQueueDragCandidate();
        var data = new DataObject(QueueItemsDragFormat, payload);
        try
        {
            DragDrop.DoDragDrop(JobsList, data, DragDropEffects.Move);
        }
        finally
        {
            HideQueueDropIndicator();
            ResetQueueDragCandidate();
        }
    }

    private void ResetQueueDragCandidate()
    {
        _queueDragItem = null;
        _queueDragItemWasSelected = false;
        _queueSelectionBeforeMouseDown = [];
    }

    private void JobsList_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (IsInteractionLocked
            || e.Data.GetData(QueueItemsDragFormat) is not QueueDragPayload payload)
        {
            return;
        }

        e.Handled = true;
        AutoScrollQueue(e.GetPosition(JobsList));
        if (!TryGetQueueInsertion(e, out _, out var targetContainer, out var insertAfter)
            || targetContainer?.DataContext is QueueItemViewModel targetItem
            && payload.Items.Contains(targetItem))
        {
            e.Effects = DragDropEffects.None;
            HideQueueDropIndicator();
            return;
        }

        e.Effects = DragDropEffects.Move;
        ShowQueueDropIndicator(targetContainer, insertAfter);
    }

    private void JobsList_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(QueueItemsDragFormat))
        {
            return;
        }

        var position = e.GetPosition(JobsList);
        if (position.X < 0
            || position.Y < 0
            || position.X > JobsList.ActualWidth
            || position.Y > JobsList.ActualHeight)
        {
            HideQueueDropIndicator();
        }
    }

    private void JobsList_Drop(object sender, DragEventArgs e)
    {
        if (IsInteractionLocked
            || e.Data.GetData(QueueItemsDragFormat) is not QueueDragPayload payload)
        {
            return;
        }

        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            if (!TryGetQueueInsertion(
                    e,
                    out var rawBoundary,
                    out var targetContainer,
                    out _))
            {
                return;
            }

            if (targetContainer?.DataContext is QueueItemViewModel targetItem
                && payload.Items.Contains(targetItem))
            {
                return;
            }

            var original = Jobs.ToList();
            var movingSet = payload.Items.ToHashSet();
            var moving = original.Where(movingSet.Contains).ToList();
            if (moving.Count == 0)
            {
                return;
            }

            rawBoundary = Math.Clamp(rawBoundary, 0, original.Count);
            var removedBeforeBoundary = original
                .Take(rawBoundary)
                .Count(movingSet.Contains);
            var remaining = original.Where(item => !movingSet.Contains(item)).ToList();
            var insertAt = Math.Clamp(
                rawBoundary - removedBeforeBoundary,
                0,
                remaining.Count);
            var finalOrder = new List<QueueItemViewModel>(original.Count);
            finalOrder.AddRange(remaining.Take(insertAt));
            finalOrder.AddRange(moving);
            finalOrder.AddRange(remaining.Skip(insertAt));

            if (!original.SequenceEqual(finalOrder))
            {
                ApplyQueueOrder(finalOrder);
                ClearQueueSortIndicators();
            }

            RestoreQueueSelection(moving, payload.PrimaryItem);
            e.Effects = DragDropEffects.Move;
        }
        finally
        {
            HideQueueDropIndicator();
        }
    }

    private bool TryGetQueueInsertion(
        DragEventArgs e,
        out int rawBoundary,
        out ListBoxItem? targetContainer,
        out bool insertAfter)
    {
        rawBoundary = 0;
        targetContainer = null;
        insertAfter = false;
        var source = e.OriginalSource as DependencyObject;
        if (source is null || FindVisualParent<ScrollBar>(source) is not null)
        {
            return false;
        }

        targetContainer = ItemsControl.ContainerFromElement(JobsList, source) as ListBoxItem;
        if (targetContainer?.DataContext is QueueItemViewModel targetItem)
        {
            var targetIndex = Jobs.IndexOf(targetItem);
            if (targetIndex < 0)
            {
                return false;
            }

            insertAfter = e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2;
            rawBoundary = targetIndex + (insertAfter ? 1 : 0);
            return true;
        }

        var position = e.GetPosition(JobsList);
        if (position.X < 0
            || position.Y < 0
            || position.X > JobsList.ActualWidth
            || position.Y > JobsList.ActualHeight)
        {
            return false;
        }

        rawBoundary = Jobs.Count;
        insertAfter = true;
        return true;
    }

    private void ShowQueueDropIndicator(ListBoxItem? targetContainer, bool insertAfter)
    {
        double y;
        if (targetContainer is not null)
        {
            y = targetContainer
                .TransformToAncestor(QueueListSurface)
                .Transform(new Point(0, insertAfter ? targetContainer.ActualHeight : 0))
                .Y;
        }
        else if (Jobs.Count > 0
                 && JobsList.ItemContainerGenerator.ContainerFromItem(Jobs[^1])
                     is ListBoxItem lastContainer)
        {
            y = lastContainer
                .TransformToAncestor(QueueListSurface)
                .Transform(new Point(0, lastContainer.ActualHeight))
                .Y;
        }
        else
        {
            y = Jobs.Count == 0 ? 0 : QueueListSurface.ActualHeight - 3;
        }

        QueueDropIndicatorTransform.Y = Math.Clamp(
            y - 1.5,
            0,
            Math.Max(0, QueueListSurface.ActualHeight - 3));
        QueueDropIndicator.Visibility = Visibility.Visible;
    }

    private void HideQueueDropIndicator() =>
        QueueDropIndicator.Visibility = Visibility.Collapsed;

    private void AutoScrollQueue(Point position)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(JobsList);
        if (scrollViewer is null)
        {
            return;
        }

        const double edgeSize = 28;
        const double scrollAmount = 18;
        if (position.Y < edgeSize)
        {
            scrollViewer.ScrollToVerticalOffset(
                Math.Max(0, scrollViewer.VerticalOffset - scrollAmount));
        }
        else if (position.Y > JobsList.ActualHeight - edgeSize)
        {
            scrollViewer.ScrollToVerticalOffset(
                Math.Min(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset + scrollAmount));
        }
    }

    private void ClearQueueSortIndicators()
    {
        _queueSortProperty = null;
        _queueSortDirection = null;
        UpdateQueueSortIndicators();
    }

    private void UpdateQueueSortIndicators()
    {
        if (NameSortGlyph is null)
        {
            return;
        }

        HideQueueSortGlyphs();
        if (_queueSortDirection is null || _queueSortProperty is null)
        {
            return;
        }

        var glyph = _queueSortDirection == ListSortDirection.Ascending ? "▲" : "▼";
        switch (_queueSortProperty)
        {
            case nameof(QueueItemViewModel.Name):
                NameSortGlyph.Text = glyph;
                break;
            case nameof(QueueItemViewModel.DetectedFiles):
                DetectedFilesSortGlyph.Text = glyph;
                break;
            case nameof(QueueItemViewModel.MediaFormatText):
                MediaFormatSortGlyph.Text = glyph;
                break;
            case nameof(QueueItemViewModel.VideoCodecText):
                VideoCodecSortGlyph.Text = glyph;
                break;
            case nameof(QueueItemViewModel.PlanDescription):
                PlanDescriptionSortGlyph.Text = glyph;
                break;
            case nameof(QueueItemViewModel.StatusText):
                StatusTextSortGlyph.Text = glyph;
                break;
        }
    }

    private void HideQueueSortGlyphs()
    {
        NameSortGlyph.Text = string.Empty;
        DetectedFilesSortGlyph.Text = string.Empty;
        MediaFormatSortGlyph.Text = string.Empty;
        VideoCodecSortGlyph.Text = string.Empty;
        PlanDescriptionSortGlyph.Text = string.Empty;
        StatusTextSortGlyph.Text = string.Empty;
    }

    private void RestoreQueueSelection(
        IEnumerable<QueueItemViewModel> selectedItems,
        QueueItemViewModel? currentItem)
    {
        var availableItems = selectedItems
            .Where(Jobs.Contains)
            .Distinct()
            .ToArray();
        JobsList.SelectedItems.Clear();
        if (currentItem is not null && availableItems.Contains(currentItem))
        {
            JobsList.SelectedItems.Add(currentItem);
        }

        foreach (var item in availableItems)
        {
            if (!ReferenceEquals(item, currentItem))
            {
                JobsList.SelectedItems.Add(item);
            }
        }

        var itemToReveal = currentItem is not null && availableItems.Contains(currentItem)
            ? currentItem
            : availableItems.FirstOrDefault();
        if (itemToReveal is not null)
        {
            JobsList.ScrollIntoView(itemToReveal);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent)
            {
                return parent;
            }

            child = GetVisualOrLogicalParent(child);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void JobsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateControls();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (!IsDescendantOf(source, QueueHeader))
        {
            HideQueueSortGlyphs();
            ClearQueueHeaderFocus();
        }
    }

    private void ClearQueueHeaderFocus()
    {
        if (Keyboard.FocusedElement is DependencyObject focused
            && IsDescendantOf(focused, QueueHeader))
        {
            Keyboard.ClearFocus();
        }
    }

    private static bool IsDescendantOf(
        DependencyObject child,
        DependencyObject ancestor)
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = GetVisualOrLogicalParent(current);
        }

        return false;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject child)
    {
        if (child is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(child);
        }

        return child is FrameworkContentElement contentElement
            ? contentElement.Parent
            : LogicalTreeHelper.GetParent(child);
    }

    private void RecursiveCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _settings.IncludeSubdirectories = RecursiveCheckBox.IsChecked == true;
        if (IsLoaded)
        {
            _settings.Save();
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsInteractionLocked)
        {
            return;
        }

        var dialog = new SettingsWindow(_settings.Copy()) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var matchingChanged = _settings.AllowSubtitleSuffixMatch != dialog.Settings.AllowSubtitleSuffixMatch;
            _settings = dialog.Settings;
            RefreshQueueColumnPresentation();
            RecursiveCheckBox.IsChecked = _settings.IncludeSubdirectories;
            foreach (var job in Jobs)
            {
                job.RefreshPresentation(_settings);
            }

            AppendLog("설정을 저장했습니다.");
            if (matchingChanged && Jobs.Count > 0)
            {
                var inputs = Jobs
                    .SelectMany(static job => job.Media.CandidateVideoPaths.Concat(new[]
                    {
                        job.Media.AssPath,
                        job.Media.SrtPath,
                        job.Media.SmiPath
                    }.OfType<string>()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                await AddPathsAsync(inputs, replaceQueue: true);
                AppendLog("자막 파일명 매칭 방식에 맞춰 현재 큐를 다시 구성했습니다.");
            }

            RefreshDependencies();
            UpdateControls();
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsInteractionLocked)
        {
            return;
        }

        RefreshDependencies();
        if (_dependencies is null || !_dependencies.IsReady)
        {
            MessageBox.Show(
                this,
                "mkvmerge와 seconv 경로를 먼저 설정해 주세요.",
                "Dependency 없음",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var batchSettings = _settings.Copy();
        try
        {
            batchSettings.Validate();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "설정 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ClearQueueSortIndicators();
        var targets = Jobs.Where(static job => job.IsValid).ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        var batchDependencies = _dependencies;
        var workerCount = Math.Min(batchSettings.ConcurrentJobCount, targets.Length);
        var processingCancellation = new CancellationTokenSource();
        CloseCompletionNotification();
        _processingCancellation = processingCancellation;
        SetBusy(true);
        AppendLog($"배치 작업을 시작합니다. 대상: {targets.Length}개 · 동시 작업: {workerCount}개");

        foreach (var target in targets)
        {
            target.State = JobState.Queued;
            target.Progress = 0;
            target.OutputPath = null;
            target.Error = null;
        }
        SetOverallProgress(0);

        var progressByJob = new double[targets.Length];
        var terminal = new bool[targets.Length];
        var nextTargetIndex = 0;
        var finishedCount = 0;
        var activeCount = 0;
        CompletionFeedback? completionFeedback = null;

        void RefreshBatchProgress()
        {
            var percent = progressByJob.Average();
            if (processingCancellation.IsCancellationRequested)
            {
                OverallProgressBar.IsIndeterminate = false;
                OverallProgressBar.Value = percent;
                TaskbarProgressInfo.ProgressValue = Math.Clamp(percent / 100d, 0d, 1d);
                TaskbarProgressInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
                return;
            }

            SetOverallProgress(percent);
        }

        void RefreshBatchStatus()
        {
            var waitingCount = Math.Max(0, targets.Length - Math.Min(nextTargetIndex, targets.Length));
            OverallStatusText.Text =
                $"완료 {finishedCount}/{targets.Length} · 실행 {activeCount} · 대기 {waitingCount}";
        }

        async Task ProcessTargetAsync(int index)
        {
            var target = targets[index];
            RevealStartedTarget(target);
            activeCount++;
            AppendLog($"[{target.Name}] {target.PlanDescription}");
            RefreshBatchStatus();

            var lastMessage = string.Empty;
            var progress = new Progress<JobProgress>(update =>
            {
                if (terminal[index]
                    || (processingCancellation.IsCancellationRequested && target.State == JobState.Cancelling))
                {
                    return;
                }

                target.ApplyProgress(update);
                progressByJob[index] = Math.Max(progressByJob[index], Math.Clamp(update.Percent, 0, 100));
                RefreshBatchProgress();
                if (!string.IsNullOrWhiteSpace(update.Message)
                    && !string.Equals(lastMessage, update.Message, StringComparison.Ordinal))
                {
                    lastMessage = update.Message;
                    AppendLog($"[{target.Name}] {update.Message}");
                }
            });

            try
            {
                var processor = new BatchProcessor(new ExternalProcessRunner());
                var result = await processor.ProcessAsync(
                    target.Media,
                    target.Plan,
                    batchSettings,
                    batchDependencies,
                    progress,
                    processingCancellation.Token);

                terminal[index] = true;
                target.State = result.State;
                target.OutputPath = result.OutputPath;
                target.Error = result.Error;
                if (result.State is JobState.Succeeded or JobState.SucceededWithWarnings or JobState.Skipped)
                {
                    target.Progress = 100;
                }

                progressByJob[index] = 100;
                foreach (var warning in result.Warnings)
                {
                    AppendLog($"[{target.Name}] 경고: {warning}");
                }

                if (result.Error is not null)
                {
                    var resultLabel = result.State == JobState.Skipped ? "건너뜀" : "실패";
                    AppendLog($"[{target.Name}] {resultLabel}: {result.Error}");
                }
            }
            catch (OperationCanceledException)
            {
                terminal[index] = true;
                target.State = JobState.Cancelled;
                AppendLog($"[{target.Name}] 작업이 취소되었습니다.");
            }
            catch (Exception exception)
            {
                terminal[index] = true;
                target.State = JobState.Failed;
                target.Error = exception.Message;
                progressByJob[index] = 100;
                AppendLog($"[{target.Name}] 예기치 않은 실패: {exception.Message}");
            }
            finally
            {
                activeCount--;
                finishedCount++;
                RefreshBatchProgress();
                RefreshBatchStatus();
            }
        }

        async Task RunWorkerAsync()
        {
            while (!processingCancellation.IsCancellationRequested)
            {
                var index = nextTargetIndex++;
                if (index >= targets.Length)
                {
                    return;
                }

                await ProcessTargetAsync(index);
            }
        }

        try
        {
            var workers = Enumerable.Range(0, workerCount)
                .Select(_ => RunWorkerAsync())
                .ToArray();
            await Task.WhenAll(workers);

            if (processingCancellation.IsCancellationRequested)
            {
                for (var index = 0; index < targets.Length; index++)
                {
                    if (targets[index].State is JobState.Queued or JobState.Cancelling)
                    {
                        terminal[index] = true;
                        targets[index].State = JobState.Cancelled;
                    }
                }

                OverallStatusText.Text = "배치 작업을 취소했습니다.";
                ClearOverallProgress();
                AppendLog(OverallStatusText.Text);
            }
            else
            {
                var succeeded = targets.Count(static job => job.State == JobState.Succeeded);
                var warnings = targets.Count(static job => job.State == JobState.SucceededWithWarnings);
                var failed = targets.Count(static job => job.State == JobState.Failed);
                var skipped = targets.Count(static job => job.State == JobState.Skipped);
                OverallStatusText.Text = $"완료 {succeeded + warnings} · 실패 {failed} · 건너뜀 {skipped}";
                SetOverallProgress(
                    100,
                    failed > 0 ? TaskbarItemProgressState.Error : TaskbarItemProgressState.Normal);
                AppendLog(OverallStatusText.Text);
                completionFeedback = new CompletionFeedback(succeeded, warnings, failed, skipped);
            }
        }
        catch (Exception exception)
        {
            AppendLog($"배치 실행 실패: {exception.Message}");
            OverallStatusText.Text = "배치 실행 중 오류가 발생했습니다.";
            SetOverallProgress(100, TaskbarItemProgressState.Error);
            MessageBox.Show(this, exception.Message, "배치 실행 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_processingCancellation, processingCancellation))
            {
                _processingCancellation = null;
            }

            processingCancellation.Dispose();
            SetBusy(false);
            if (completionFeedback is not null && !_closeWhenIdle)
            {
                ShowCompletionFeedback(batchSettings, completionFeedback);
            }
            CloseWhenIdleIfRequested();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        RequestProcessingCancellation("현재 외부 프로세스를 종료하고 있습니다…", "사용자가 배치 취소를 요청했습니다.");
    }

    private void RefreshQueueColumnWidths()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompositionColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaFormatColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoCodecColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileColumnResizeVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompositionColumnResizeVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaFormatColumnResizeVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoCodecColumnResizeVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkColumnResizeVisibility)));
    }
    private void RefreshQueueColumnPresentation()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileColumnVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompositionColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompositionColumnVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaFormatColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaFormatColumnVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoCodecColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VideoCodecColumnVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkColumnVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColumnWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColumnVisibility)));

        if (_queueSortProperty is not null && !IsQueueColumnVisible(_queueSortProperty))
        {
            ClearQueueSortIndicators();
        }
    }

    private bool CanResizeQueueColumn(string propertyName) =>
        IsQueueColumnVisible(propertyName) && GetNextVisibleQueueColumn(propertyName) is not null;

    private string? GetNextVisibleQueueColumn(string propertyName)
    {
        var index = Array.IndexOf(QueueColumnProperties, propertyName);
        for (var next = index + 1; index >= 0 && next < QueueColumnProperties.Length; next++)
        {
            if (IsQueueColumnVisible(QueueColumnProperties[next]))
            {
                return QueueColumnProperties[next];
            }
        }

        return null;
    }

    private double GetQueueColumnWeight(string propertyName) => propertyName switch
    {
        nameof(QueueItemViewModel.Name) => _settings.FileColumnWeight,
        nameof(QueueItemViewModel.DetectedFiles) => _settings.CompositionColumnWeight,
        nameof(QueueItemViewModel.MediaFormatText) => _settings.MediaFormatColumnWeight,
        nameof(QueueItemViewModel.VideoCodecText) => _settings.VideoCodecColumnWeight,
        nameof(QueueItemViewModel.PlanDescription) => _settings.WorkColumnWeight,
        nameof(QueueItemViewModel.StatusText) => _settings.StatusColumnWeight,
        _ => 0
    };

    private void SetQueueColumnWeight(string propertyName, double weight)
    {
        switch (propertyName)
        {
            case nameof(QueueItemViewModel.Name): _settings.FileColumnWeight = weight; break;
            case nameof(QueueItemViewModel.DetectedFiles): _settings.CompositionColumnWeight = weight; break;
            case nameof(QueueItemViewModel.MediaFormatText): _settings.MediaFormatColumnWeight = weight; break;
            case nameof(QueueItemViewModel.VideoCodecText): _settings.VideoCodecColumnWeight = weight; break;
            case nameof(QueueItemViewModel.PlanDescription): _settings.WorkColumnWeight = weight; break;
            case nameof(QueueItemViewModel.StatusText): _settings.StatusColumnWeight = weight; break;
        }
    }

    private bool IsQueueColumnVisible(string propertyName) => propertyName switch
    {
        nameof(QueueItemViewModel.Name) => _settings.ShowFileColumn,
        nameof(QueueItemViewModel.DetectedFiles) => _settings.ShowCompositionColumn,
        nameof(QueueItemViewModel.MediaFormatText) => _settings.ShowMediaFormatColumn,
        nameof(QueueItemViewModel.VideoCodecText) => _settings.ShowVideoCodecColumn,
        nameof(QueueItemViewModel.PlanDescription) => _settings.ShowWorkColumn,
        nameof(QueueItemViewModel.StatusText) => _settings.ShowStatusColumn,
        _ => false
    };

    private int CountVisibleQueueColumns() =>
        (_settings.ShowFileColumn ? 1 : 0)
        + (_settings.ShowCompositionColumn ? 1 : 0)
        + (_settings.ShowMediaFormatColumn ? 1 : 0)
        + (_settings.ShowVideoCodecColumn ? 1 : 0)
        + (_settings.ShowWorkColumn ? 1 : 0)
        + (_settings.ShowStatusColumn ? 1 : 0);

    private bool SetQueueColumnVisible(string propertyName, bool visible)
    {
        switch (propertyName)
        {
            case nameof(QueueItemViewModel.Name):
                _settings.ShowFileColumn = visible;
                return true;
            case nameof(QueueItemViewModel.DetectedFiles):
                _settings.ShowCompositionColumn = visible;
                return true;
            case nameof(QueueItemViewModel.MediaFormatText):
                _settings.ShowMediaFormatColumn = visible;
                return true;
            case nameof(QueueItemViewModel.VideoCodecText):
                _settings.ShowVideoCodecColumn = visible;
                return true;
            case nameof(QueueItemViewModel.PlanDescription):
                _settings.ShowWorkColumn = visible;
                return true;
            case nameof(QueueItemViewModel.StatusText):
                _settings.ShowStatusColumn = visible;
                return true;
            default:
                return false;
        }
    }

    private void RefreshDependencies()
    {
        _dependencies = _dependencyLocator.Locate(_settings.MkvMergePath, _settings.SeConvPath);
        SetDependencyStatus(MkvStatusDot, MkvStatusText, _dependencies.MkvMerge);
        SetDependencyStatus(SeConvStatusDot, SeConvStatusText, _dependencies.SeConv);
    }

    private static void SetDependencyStatus(
        System.Windows.Shapes.Ellipse dot,
        System.Windows.Controls.TextBlock text,
        ToolDependency dependency)
    {
        dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            dependency.IsAvailable ? "#22C55E" : "#EF4444"));
        text.Text = dependency.IsAvailable
            ? $"사용 가능 · {FormatVersion(dependency.Version)}"
            : "찾지 못함 — 설정에서 경로를 지정하세요";
        text.ToolTip = dependency.Path;
    }

    private static string FormatVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "버전 미상";
        }

        var separator = version.IndexOfAny(['+', ' ', '-']);
        return separator > 0 ? version[..separator] : version;
    }

    private void SetOverallProgress(
        double percent,
        TaskbarItemProgressState taskbarState = TaskbarItemProgressState.Normal)
    {
        percent = Math.Clamp(percent, 0d, 100d);
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = percent;
        TaskbarProgressInfo.ProgressValue = percent / 100d;
        TaskbarProgressInfo.ProgressState = taskbarState;
    }

    private void SetOverallIndeterminate()
    {
        OverallProgressBar.IsIndeterminate = true;
        TaskbarProgressInfo.ProgressValue = 0;
        TaskbarProgressInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
    }

    private void ClearOverallProgress()
    {
        OverallProgressBar.IsIndeterminate = false;
        OverallProgressBar.Value = 0;
        TaskbarProgressInfo.ProgressValue = 0;
        TaskbarProgressInfo.ProgressState = TaskbarItemProgressState.None;
    }

    private void RevealStartedTarget(QueueItemViewModel target)
    {
        if (!Jobs.Contains(target))
        {
            return;
        }

        var requestVersion = ++_queueRevealVersion;
        JobsList.ScrollIntoView(target);
        JobsList.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (requestVersion != _queueRevealVersion || !Jobs.Contains(target))
                {
                    return;
                }

                JobsList.UpdateLayout();
                if (JobsList.ItemContainerGenerator.ContainerFromItem(target) is not ListBoxItem row
                    || FindVisualChild<ScrollViewer>(JobsList) is not { } scrollViewer
                    || scrollViewer.ViewportHeight <= 0)
                {
                    return;
                }

                try
                {
                    var rowBottom = row.TransformToAncestor(scrollViewer)
                        .Transform(new Point(0, row.ActualHeight)).Y;
                    const double bottomPadding = 8;
                    var desiredBottom = Math.Max(0, scrollViewer.ViewportHeight - bottomPadding);
                    var offset = Math.Clamp(
                        scrollViewer.VerticalOffset + rowBottom - desiredBottom,
                        0,
                        scrollViewer.ScrollableHeight);
                    if (Math.Abs(offset - scrollViewer.VerticalOffset) > 0.5)
                    {
                        scrollViewer.ScrollToVerticalOffset(offset);
                    }
                }
                catch (InvalidOperationException)
                {
                    // A recycled virtualized row can leave the visual tree between layout passes.
                }
            }));
    }

    private void RequestProcessingCancellation(string status, string logMessage)
    {
        if (_processingCancellation is null)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        OverallStatusText.Text = status;
        foreach (var item in Jobs.Where(static job => job.State is JobState.Queued
                     or JobState.Muxing
                     or JobState.ConvertingAssToSrt
                     or JobState.ConvertingSmiToSrt
                     or JobState.ConvertingSrtToAss
                     or JobState.Verifying))
        {
            item.State = JobState.Cancelling;
        }

        TaskbarProgressInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
        _processingCancellation.Cancel();
        AppendLog(logMessage);
    }

    private void ShowCompletionFeedback(AppSettings settings, CompletionFeedback feedback)
    {
        if (settings.PlayCompletionSound)
        {
            try
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch (Exception exception)
            {
                AppendLog($"완료 알림음 재생 실패: {exception.Message}");
            }
        }

        if (!settings.ShowCompletionNotification)
        {
            return;
        }

        try
        {
            var hasFailures = feedback.Failed > 0;
            var hasWarnings = feedback.Warnings > 0;
            var noSuccessfulJobs = feedback.Succeeded == 0 && feedback.Warnings == 0;
            var title = hasFailures
                ? noSuccessfulJobs
                    ? "배치 작업 실패"
                    : "일부 작업 실패"
                : hasWarnings
                    ? "경고와 함께 완료"
                    : "배치 작업 완료";
            var summary =
                $"정상 완료 {feedback.Succeeded} · 경고 완료 {feedback.Warnings} · 실패 {feedback.Failed} · 건너뜀 {feedback.Skipped}";
            var kind = hasFailures
                ? CompletionNotificationKind.Failure
                : hasWarnings
                    ? CompletionNotificationKind.Warning
                    : CompletionNotificationKind.Success;

            CloseCompletionNotification();
            var notification = new CompletionNotificationWindow(this, title, summary, kind);
            notification.Closed += (_, _) =>
            {
                if (ReferenceEquals(_completionNotificationWindow, notification))
                {
                    _completionNotificationWindow = null;
                }
            };
            _completionNotificationWindow = notification;
            notification.Show();
        }
        catch (Exception exception)
        {
            CloseCompletionNotification();
            AppendLog($"완료 알림 표시 실패: {exception.Message}");
        }
    }

    private void CloseCompletionNotification()
    {
        var notification = _completionNotificationWindow;
        _completionNotificationWindow = null;
        notification?.Close();
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        UpdateControls();
    }

    private void UpdateControls()
    {
        EmptyDropPanel.Visibility = Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var locked = IsInteractionLocked;
        var hasRunnable = Jobs.Any(static job => job.IsValid);
        StartButton.IsEnabled = !locked && hasRunnable && _dependencies?.IsReady == true;
        AddFilesButton.IsEnabled = !locked;
        AddFolderButton.IsEnabled = !locked;
        SettingsButton.IsEnabled = !locked;
        QueueHeader.IsEnabled = !locked;
        RemoveButton.IsEnabled = !locked && JobsList.SelectedItems.Count > 0;
        ClearButton.IsEnabled = !locked && Jobs.Count > 0;
        RecursiveCheckBox.IsEnabled = !locked;
        CancelButton.IsEnabled = _isBusy && _processingCancellation?.IsCancellationRequested == false;
    }

    private void OpenLogWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is not null)
        {
            if (_logWindow.WindowState == WindowState.Minimized)
            {
                _logWindow.WindowState = WindowState.Normal;
            }

            _logWindow.Activate();
            return;
        }

        var logDirectory = _logger?.LogDirectory
                           ?? Path.Combine(AppSettings.SettingsDirectory, "logs");
        var logPath = _logger?.LogPath
                      ?? Path.Combine(logDirectory, "현재 세션 로그");
        var window = new LogWindow(_sessionLog.ToString(), logPath, logDirectory)
        {
            Owner = this
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_logWindow, window))
            {
                _logWindow = null;
            }
        };
        _logWindow = window;
        window.Show();
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(QueueItemsDragFormat))
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Handled = true;
        if (IsInteractionLocked
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths
            || !paths.Any(IsSupportedDropPath))
        {
            e.Effects = DragDropEffects.None;
            DragOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DragOverlay.Visibility = Visibility.Visible;
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetDataPresent(QueueItemsDragFormat))
        {
            HideQueueDropIndicator();
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(QueueItemsDragFormat))
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.Handled = true;
        DragOverlay.Visibility = Visibility.Collapsed;
        if (IsInteractionLocked
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths
            || !paths.Any(IsSupportedDropPath))
        {
            return;
        }

        await AddPathsAsync(paths);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsInteractionLocked)
        {
            if (!_closeWhenIdle)
            {
                var answer = MessageBox.Show(
                    this,
                    "진행 중인 작업을 취소한 뒤 창을 닫을까요?",
                    "SubMux Batch",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    _closeWhenIdle = true;
                    RequestProcessingCancellation(
                        "작업을 취소한 뒤 창을 닫습니다…",
                        "창을 닫기 위해 배치 취소를 요청했습니다.");
                    _scanCancellation?.Cancel();
                    if (_scanCancellation is not null)
                    {
                        TaskbarProgressInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
                        OverallStatusText.Text = "작업을 취소한 뒤 창을 닫습니다…";
                    }
                }
            }

            e.Cancel = true;
            return;
        }

        _logWindow?.Close();
        CloseCompletionNotification();
        _logger?.Dispose();
    }

    private static bool IsSupportedDropPath(string path)
    {
        if (Directory.Exists(path))
        {
            return true;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        return MediaInputFormats.IsSupported(path);

    }

    private void CloseWhenIdleIfRequested()
    {
        if (!_closeWhenIdle || IsInteractionLocked)
        {
            return;
        }

        _closeWhenIdle = false;
        Dispatcher.BeginInvoke(Close);
    }

    private void AppendLog(string message)
    {
        var line = _logger?.Write(message) ?? $"[{DateTime.Now:HH:mm:ss}] {message}";
        _sessionLog.AppendLine(line);
        if (_sessionLog.Length > 200_000)
        {
            _sessionLog.Remove(0, _sessionLog.Length - 150_000);
        }

        _logWindow?.AppendLine(line);
    }

}
