using System.Globalization;
using System.Windows;
using SubMuxBatch.App.Services;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.App;

public partial class AssStyleDetailsWindow : Window
{
    private static readonly IReadOnlyList<AlignmentOption> AlignmentOptions =
    [
        new(1, "아래 왼쪽"),
        new(2, "아래 가운데"),
        new(3, "아래 오른쪽"),
        new(4, "가운데 왼쪽"),
        new(5, "가운데"),
        new(6, "가운데 오른쪽"),
        new(7, "위 왼쪽"),
        new(8, "위 가운데"),
        new(9, "위 오른쪽")
    ];

    private AssStyleDefinition _styleDefinition;

    public AssStyleDetailsWindow(int playResX, int playResY, string? assStyleLine)
    {
        InitializeComponent();

        PlayResX = playResX;
        PlayResY = playResY;
        _styleDefinition = ParseStyleOrDefault(assStyleLine);
        AssStyleLine = _styleDefinition.ToStyleLine();

        AlignmentComboBox.ItemsSource = AlignmentOptions;
        PlayResXTextBox.Text = playResX.ToString(CultureInfo.InvariantCulture);
        PlayResYTextBox.Text = playResY.ToString(CultureInfo.InvariantCulture);
        PopulateStyleFields(_styleDefinition);

        Loaded += (_, _) => WindowPlacementHelper.FitToCurrentWorkingArea(this);
    }

    public int PlayResX { get; private set; }

    public int PlayResY { get; private set; }

    public string AssStyleLine { get; private set; }

    private void OpenManualStyleInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ManualAssStyleInputWindow(_styleDefinition.ToStyleLine())
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.StyleDefinition is not null)
        {
            _styleDefinition = dialog.StyleDefinition;
            PopulateStyleFields(_styleDefinition);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseResolution(PlayResXTextBox.Text, out var playResX)
            || !TryParseResolution(PlayResYTextBox.Text, out var playResY))
        {
            ShowValidationError("PlayResX와 PlayResY에는 16~16384 범위의 정수를 입력해 주세요.");
            return;
        }

        try
        {
            // Work on a clone so a failed validation never partly mutates the
            // last valid definition or its hidden fields.
            var updated = AssStyleDefinition.Parse(_styleDefinition.ToStyleLine());
            updated.FontName = FontNameTextBox.Text;
            updated.FontSize = ParseDouble(FontSizeTextBox.Text, "폰트 크기");
            updated.PrimaryColour = PrimaryColorTextBox.Text;
            updated.OutlineColour = OutlineColorTextBox.Text;
            updated.BackColour = BackColorTextBox.Text;
            updated.Bold = BoldCheckBox.IsChecked == true;
            updated.Italic = ItalicCheckBox.IsChecked == true;
            updated.Outline = ParseDouble(OutlineWidthTextBox.Text, "외곽선 두께");
            updated.Shadow = ParseDouble(ShadowDepthTextBox.Text, "그림자 깊이");
            updated.Alignment = AlignmentComboBox.SelectedItem is AlignmentOption alignment
                ? alignment.Value
                : throw new FormatException("정렬은 1부터 9까지 선택해 주세요.");
            updated.MarginLeft = ParseInteger(MarginLeftTextBox.Text, "좌측 여백");
            updated.MarginRight = ParseInteger(MarginRightTextBox.Text, "우측 여백");
            updated.MarginVertical = ParseInteger(MarginVerticalTextBox.Text, "수직 여백");
            updated.Validate();

            var styleLine = updated.ToStyleLine();
            new AppSettings
            {
                UseCustomAssStyle = true,
                PlayResX = playResX,
                PlayResY = playResY,
                AssStyleLine = styleLine
            }.Validate();

            _styleDefinition = updated;
            PlayResX = playResX;
            PlayResY = playResY;
            AssStyleLine = styleLine;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            ShowValidationError(exception.Message);
        }
    }

    private void PopulateStyleFields(AssStyleDefinition definition)
    {
        FontNameTextBox.Text = definition.FontName;
        FontSizeTextBox.Text = FormatNumber(definition.FontSize);
        PrimaryColorTextBox.Text = definition.PrimaryColour;
        OutlineColorTextBox.Text = definition.OutlineColour;
        BackColorTextBox.Text = definition.BackColour;
        BoldCheckBox.IsChecked = definition.Bold;
        ItalicCheckBox.IsChecked = definition.Italic;
        OutlineWidthTextBox.Text = FormatNumber(definition.Outline);
        ShadowDepthTextBox.Text = FormatNumber(definition.Shadow);
        AlignmentComboBox.SelectedItem = AlignmentOptions.First(option => option.Value == definition.Alignment);
        MarginLeftTextBox.Text = definition.MarginLeft.ToString(CultureInfo.InvariantCulture);
        MarginRightTextBox.Text = definition.MarginRight.ToString(CultureInfo.InvariantCulture);
        MarginVerticalTextBox.Text = definition.MarginVertical.ToString(CultureInfo.InvariantCulture);
    }

    private void ShowValidationError(string message) =>
        MessageBox.Show(
            this,
            message,
            "ASS 설정 확인",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double ParseDouble(string value, string label)
    {
        if ((double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var result)
                || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            && double.IsFinite(result))
        {
            return result;
        }

        throw new FormatException($"{label}에는 올바른 숫자를 입력해 주세요.");
    }

    private static int ParseInteger(string value, string label)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{label}에는 정수를 입력해 주세요.");
    }

    private static bool TryParseResolution(string value, out int resolution) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out resolution)
        && resolution is >= 16 and <= 16384;

    private static AssStyleDefinition ParseStyleOrDefault(string? styleLine) =>
        AssStyleDefinition.TryParse(styleLine, out var definition)
            ? definition!
            : AssStyleDefinition.Parse(AppSettings.DefaultAssStyleLine);

    private sealed record AlignmentOption(int Value, string Label)
    {
        public override string ToString() => $"{Value} · {Label}";
    }
}
