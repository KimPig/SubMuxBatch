using System.Globalization;
using System.Text.RegularExpressions;
using SubMuxBatch.Core.Localization;

namespace SubMuxBatch.Core.Configuration;

/// <summary>
/// A lossless, editable representation of one ASS V4+ <c>Style:</c> line.
/// Values not exposed as strongly typed properties remain available through
/// <see cref="Fields"/> and are preserved when the definition is serialized.
/// </summary>
public sealed class AssStyleDefinition
{
    public const int FieldCount = 23;

    private const int NameIndex = 0;
    private const int FontNameIndex = 1;
    private const int FontSizeIndex = 2;
    private const int PrimaryColourIndex = 3;
    private const int SecondaryColourIndex = 4;
    private const int OutlineColourIndex = 5;
    private const int BackColourIndex = 6;
    private const int BoldIndex = 7;
    private const int ItalicIndex = 8;
    private const int UnderlineIndex = 9;
    private const int StrikeOutIndex = 10;
    private const int ScaleXIndex = 11;
    private const int ScaleYIndex = 12;
    private const int SpacingIndex = 13;
    private const int AngleIndex = 14;
    private const int BorderStyleIndex = 15;
    private const int OutlineIndex = 16;
    private const int ShadowIndex = 17;
    private const int AlignmentIndex = 18;
    private const int MarginLeftIndex = 19;
    private const int MarginRightIndex = 20;
    private const int MarginVerticalIndex = 21;
    private const int EncodingIndex = 22;

    private static readonly Regex ColourPattern = new(
        @"^&H[0-9A-F]{1,8}&?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string[] _fields;

    private AssStyleDefinition(string[] fields) => _fields = fields;

    /// <summary>
    /// Gets a snapshot of all 23 fields in ASS V4+ order. Unedited field text is
    /// preserved, except that whitespace immediately surrounding commas is trimmed.
    /// </summary>
    public IReadOnlyList<string> Fields => Array.AsReadOnly((string[])_fields.Clone());

    public string Name
    {
        get => _fields[NameIndex];
        set => SetRequiredText(NameIndex, value, Label("AssLabel_StyleName"));
    }

    public string FontName
    {
        get => _fields[FontNameIndex];
        set => SetRequiredText(FontNameIndex, value, Label("AssLabel_FontName"));
    }

    public double FontSize
    {
        get => ParseDouble(_fields[FontSizeIndex], Label("AssLabel_FontSize"));
        set => SetFiniteDouble(FontSizeIndex, value, 0, double.MaxValue, Label("AssLabel_FontSize"));
    }

    public string PrimaryColour
    {
        get => _fields[PrimaryColourIndex];
        set => SetColour(PrimaryColourIndex, value, Label("AssLabel_PrimaryColour"));
    }

    public string SecondaryColour
    {
        get => _fields[SecondaryColourIndex];
        set => SetColour(SecondaryColourIndex, value, Label("AssLabel_SecondaryColour"));
    }

    public string OutlineColour
    {
        get => _fields[OutlineColourIndex];
        set => SetColour(OutlineColourIndex, value, Label("AssLabel_OutlineColour"));
    }

    public string BackColour
    {
        get => _fields[BackColourIndex];
        set => SetColour(BackColourIndex, value, Label("AssLabel_BackColour"));
    }

    public bool Bold
    {
        get => ParseAssBoolean(_fields[BoldIndex], Label("AssLabel_Bold"));
        set => _fields[BoldIndex] = value ? "-1" : "0";
    }

    public bool Italic
    {
        get => ParseAssBoolean(_fields[ItalicIndex], Label("AssLabel_Italic"));
        set => _fields[ItalicIndex] = value ? "-1" : "0";
    }

    public bool Underline
    {
        get => ParseAssBoolean(_fields[UnderlineIndex], Label("AssLabel_Underline"));
        set => _fields[UnderlineIndex] = value ? "-1" : "0";
    }

    public bool StrikeOut
    {
        get => ParseAssBoolean(_fields[StrikeOutIndex], Label("AssLabel_StrikeOut"));
        set => _fields[StrikeOutIndex] = value ? "-1" : "0";
    }

    public double ScaleX
    {
        get => ParseDouble(_fields[ScaleXIndex], Label("AssLabel_ScaleX"));
        set => SetFiniteDouble(ScaleXIndex, value, 0, double.MaxValue, Label("AssLabel_ScaleX"));
    }

    public double ScaleY
    {
        get => ParseDouble(_fields[ScaleYIndex], Label("AssLabel_ScaleY"));
        set => SetFiniteDouble(ScaleYIndex, value, 0, double.MaxValue, Label("AssLabel_ScaleY"));
    }

    public double Spacing
    {
        get => ParseDouble(_fields[SpacingIndex], Label("AssLabel_Spacing"));
        set => SetFiniteDouble(SpacingIndex, value, double.MinValue, double.MaxValue, Label("AssLabel_Spacing"));
    }

    public double Angle
    {
        get => ParseDouble(_fields[AngleIndex], Label("AssLabel_Angle"));
        set => SetFiniteDouble(AngleIndex, value, double.MinValue, double.MaxValue, Label("AssLabel_Angle"));
    }

    public int BorderStyle
    {
        get => ParseInteger(_fields[BorderStyleIndex], Label("AssLabel_BorderStyle"));
        set
        {
            if (value is not (1 or 3))
            {
                throw new ArgumentOutOfRangeException(nameof(value), CoreText.Get("Ass_BorderStyleRange"));
            }

            _fields[BorderStyleIndex] = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public double Outline
    {
        get => ParseDouble(_fields[OutlineIndex], Label("AssLabel_Outline"));
        set => SetFiniteDouble(OutlineIndex, value, 0, double.MaxValue, Label("AssLabel_Outline"));
    }

    public double Shadow
    {
        get => ParseDouble(_fields[ShadowIndex], Label("AssLabel_Shadow"));
        set => SetFiniteDouble(ShadowIndex, value, 0, double.MaxValue, Label("AssLabel_Shadow"));
    }

    public int Alignment
    {
        get => ParseInteger(_fields[AlignmentIndex], Label("AssLabel_Alignment"));
        set
        {
            if (value is < 1 or > 9)
            {
                throw new ArgumentOutOfRangeException(nameof(value), CoreText.Get("Ass_AlignmentRange"));
            }

            _fields[AlignmentIndex] = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public int MarginLeft
    {
        get => ParseInteger(_fields[MarginLeftIndex], Label("AssLabel_MarginLeft"));
        set => SetNonNegativeInteger(MarginLeftIndex, value, Label("AssLabel_MarginLeft"));
    }

    public int MarginRight
    {
        get => ParseInteger(_fields[MarginRightIndex], Label("AssLabel_MarginRight"));
        set => SetNonNegativeInteger(MarginRightIndex, value, Label("AssLabel_MarginRight"));
    }

    public int MarginVertical
    {
        get => ParseInteger(_fields[MarginVerticalIndex], Label("AssLabel_MarginVertical"));
        set => SetNonNegativeInteger(MarginVerticalIndex, value, Label("AssLabel_MarginVertical"));
    }

    public int Encoding
    {
        get => ParseInteger(_fields[EncodingIndex], Label("AssLabel_Encoding"));
        set => _fields[EncodingIndex] = value.ToString(CultureInfo.InvariantCulture);
    }

    public static AssStyleDefinition Parse(string value)
    {
        if (!TryParse(value, out var definition, out var error))
        {
            throw new FormatException(error);
        }

        return definition!;
    }

    public static bool TryParse(string? value, out AssStyleDefinition? definition) =>
        TryParse(value, out definition, out _);

    public static bool TryParse(
        string? value,
        out AssStyleDefinition? definition,
        out string? error)
    {
        definition = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = CoreText.Get("Ass_EmptyStyle");
            return false;
        }

        if (value.Length > 8192 || value.Contains('\r') || value.Contains('\n'))
        {
            error = CoreText.Get("Ass_OneLineLimit");
            return false;
        }

        var content = value.Trim();
        if (content.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
        {
            content = content["Style:".Length..];
        }

        var fields = content.Split(',').Select(static field => field.Trim()).ToArray();
        if (fields.Length != FieldCount)
        {
            error = CoreText.Get("Ass_FieldCount", FieldCount);
            return false;
        }

        try
        {
            var candidate = new AssStyleDefinition(fields);
            candidate.Validate();
            definition = candidate;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            error = exception.Message;
            return false;
        }
    }

    public void Validate()
    {
        SetRequiredText(NameIndex, _fields[NameIndex], Label("AssLabel_StyleName"));
        SetRequiredText(FontNameIndex, _fields[FontNameIndex], Label("AssLabel_FontName"));

        ValidateFiniteRange(FontSize, 0, double.MaxValue, Label("AssLabel_FontSize"), exclusiveMinimum: true);
        ValidateColour(PrimaryColour, Label("AssLabel_PrimaryColour"));
        ValidateColour(SecondaryColour, Label("AssLabel_SecondaryColour"));
        ValidateColour(OutlineColour, Label("AssLabel_OutlineColour"));
        ValidateColour(BackColour, Label("AssLabel_BackColour"));

        _ = Bold;
        _ = Italic;
        _ = Underline;
        _ = StrikeOut;
        ValidateFiniteRange(ScaleX, 0, double.MaxValue, Label("AssLabel_ScaleX"), exclusiveMinimum: true);
        ValidateFiniteRange(ScaleY, 0, double.MaxValue, Label("AssLabel_ScaleY"), exclusiveMinimum: true);
        ValidateFiniteRange(Spacing, double.MinValue, double.MaxValue, Label("AssLabel_Spacing"));
        ValidateFiniteRange(Angle, double.MinValue, double.MaxValue, Label("AssLabel_Angle"));

        if (BorderStyle is not (1 or 3))
        {
            throw new FormatException(CoreText.Get("Ass_BorderStyleRange"));
        }

        ValidateFiniteRange(Outline, 0, double.MaxValue, Label("AssLabel_Outline"));
        ValidateFiniteRange(Shadow, 0, double.MaxValue, Label("AssLabel_Shadow"));
        if (Alignment is < 1 or > 9)
        {
            throw new FormatException(CoreText.Get("Ass_AlignmentRange"));
        }

        ValidateNonNegative(MarginLeft, Label("AssLabel_MarginLeft"));
        ValidateNonNegative(MarginRight, Label("AssLabel_MarginRight"));
        ValidateNonNegative(MarginVertical, Label("AssLabel_MarginVertical"));
        _ = Encoding;
    }

    public string ToStyleLine()
    {
        Validate();
        return "Style: " + string.Join(',', _fields);
    }

    public override string ToString() => ToStyleLine();

    /// <summary>
    /// Replaces one of the less commonly edited V4+ fields by its zero-based
    /// index. The whole definition is validated before the change is committed.
    /// </summary>
    public void SetField(int index, string value)
    {
        if (index is < 0 or >= FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException(CoreText.Get("Ass_FieldNoCommaOrNewline"), nameof(value));
        }

        var previous = _fields[index];
        _fields[index] = value.Trim();
        try
        {
            Validate();
        }
        catch
        {
            _fields[index] = previous;
            throw;
        }
    }

    private void SetRequiredText(int index, string? value, string label)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Contains(',') || trimmed.Contains('\r') || trimmed.Contains('\n'))
        {
            throw new ArgumentException(CoreText.Get("Ass_InvalidField", label), nameof(value));
        }

        _fields[index] = trimmed;
    }

    private void SetColour(int index, string? value, string label)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        ValidateColour(trimmed, label);
        _fields[index] = trimmed;
    }

    private void SetFiniteDouble(int index, double value, double minimum, double maximum, string label)
    {
        ValidateFiniteRange(value, minimum, maximum, label, exclusiveMinimum: index is FontSizeIndex or ScaleXIndex or ScaleYIndex);
        _fields[index] = value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void SetNonNegativeInteger(int index, int value, string label)
    {
        ValidateNonNegative(value, label);
        _fields[index] = value.ToString(CultureInfo.InvariantCulture);
    }

    private static double ParseDouble(string value, string label)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            || !double.IsFinite(result))
        {
            throw new FormatException(CoreText.Get("Ass_InvalidNumber", label));
        }

        return result;
    }

    private static int ParseInteger(string value, string label)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException(CoreText.Get("Ass_InvalidInteger", label));
        }

        return result;
    }

    private static bool ParseAssBoolean(string value, string label)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            || result is not (-1 or 0 or 1))
        {
            throw new FormatException(CoreText.Get("Ass_InvalidBoolean", label));
        }

        return result != 0;
    }

    private static void ValidateColour(string value, string label)
    {
        if (!ColourPattern.IsMatch(value))
        {
            throw new FormatException(CoreText.Get("Ass_InvalidColour", label));
        }
    }

    private static void ValidateFiniteRange(
        double value,
        double minimum,
        double maximum,
        string label,
        bool exclusiveMinimum = false)
    {
        var belowMinimum = exclusiveMinimum ? value <= minimum : value < minimum;
        if (!double.IsFinite(value) || belowMinimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                CoreText.Get(exclusiveMinimum ? "Ass_GreaterThan" : "Ass_AtLeast", label, minimum));
        }
    }

    private static void ValidateNonNegative(int value, string label)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), CoreText.Get("Ass_NonNegative", label));
        }
    }

    private static string Label(string key) => CoreText.Get(key);
}
