using System.Globalization;
using System.Text.RegularExpressions;

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
        set => SetRequiredText(NameIndex, value, "스타일 이름");
    }

    public string FontName
    {
        get => _fields[FontNameIndex];
        set => SetRequiredText(FontNameIndex, value, "폰트 이름");
    }

    public double FontSize
    {
        get => ParseDouble(_fields[FontSizeIndex], "폰트 크기");
        set => SetFiniteDouble(FontSizeIndex, value, 0, double.MaxValue, "폰트 크기");
    }

    public string PrimaryColour
    {
        get => _fields[PrimaryColourIndex];
        set => SetColour(PrimaryColourIndex, value, "기본 색상");
    }

    public string SecondaryColour
    {
        get => _fields[SecondaryColourIndex];
        set => SetColour(SecondaryColourIndex, value, "보조 색상");
    }

    public string OutlineColour
    {
        get => _fields[OutlineColourIndex];
        set => SetColour(OutlineColourIndex, value, "외곽선 색상");
    }

    public string BackColour
    {
        get => _fields[BackColourIndex];
        set => SetColour(BackColourIndex, value, "그림자 색상");
    }

    public bool Bold
    {
        get => ParseAssBoolean(_fields[BoldIndex], "굵게");
        set => _fields[BoldIndex] = value ? "-1" : "0";
    }

    public bool Italic
    {
        get => ParseAssBoolean(_fields[ItalicIndex], "기울임꼴");
        set => _fields[ItalicIndex] = value ? "-1" : "0";
    }

    public bool Underline
    {
        get => ParseAssBoolean(_fields[UnderlineIndex], "밑줄");
        set => _fields[UnderlineIndex] = value ? "-1" : "0";
    }

    public bool StrikeOut
    {
        get => ParseAssBoolean(_fields[StrikeOutIndex], "취소선");
        set => _fields[StrikeOutIndex] = value ? "-1" : "0";
    }

    public double ScaleX
    {
        get => ParseDouble(_fields[ScaleXIndex], "가로 배율");
        set => SetFiniteDouble(ScaleXIndex, value, 0, double.MaxValue, "가로 배율");
    }

    public double ScaleY
    {
        get => ParseDouble(_fields[ScaleYIndex], "세로 배율");
        set => SetFiniteDouble(ScaleYIndex, value, 0, double.MaxValue, "세로 배율");
    }

    public double Spacing
    {
        get => ParseDouble(_fields[SpacingIndex], "글자 간격");
        set => SetFiniteDouble(SpacingIndex, value, double.MinValue, double.MaxValue, "글자 간격");
    }

    public double Angle
    {
        get => ParseDouble(_fields[AngleIndex], "회전 각도");
        set => SetFiniteDouble(AngleIndex, value, double.MinValue, double.MaxValue, "회전 각도");
    }

    public int BorderStyle
    {
        get => ParseInteger(_fields[BorderStyleIndex], "테두리 형식");
        set
        {
            if (value is not (1 or 3))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "ASS 테두리 형식은 1 또는 3이어야 합니다.");
            }

            _fields[BorderStyleIndex] = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public double Outline
    {
        get => ParseDouble(_fields[OutlineIndex], "외곽선 두께");
        set => SetFiniteDouble(OutlineIndex, value, 0, double.MaxValue, "외곽선 두께");
    }

    public double Shadow
    {
        get => ParseDouble(_fields[ShadowIndex], "그림자 깊이");
        set => SetFiniteDouble(ShadowIndex, value, 0, double.MaxValue, "그림자 깊이");
    }

    public int Alignment
    {
        get => ParseInteger(_fields[AlignmentIndex], "정렬");
        set
        {
            if (value is < 1 or > 9)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "ASS 정렬은 1부터 9까지여야 합니다.");
            }

            _fields[AlignmentIndex] = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public int MarginLeft
    {
        get => ParseInteger(_fields[MarginLeftIndex], "왼쪽 여백");
        set => SetNonNegativeInteger(MarginLeftIndex, value, "왼쪽 여백");
    }

    public int MarginRight
    {
        get => ParseInteger(_fields[MarginRightIndex], "오른쪽 여백");
        set => SetNonNegativeInteger(MarginRightIndex, value, "오른쪽 여백");
    }

    public int MarginVertical
    {
        get => ParseInteger(_fields[MarginVerticalIndex], "수직 여백");
        set => SetNonNegativeInteger(MarginVerticalIndex, value, "수직 여백");
    }

    public int Encoding
    {
        get => ParseInteger(_fields[EncodingIndex], "문자 인코딩");
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
            error = "ASS 스타일이 비어 있습니다.";
            return false;
        }

        if (value.Length > 8192 || value.Contains('\r') || value.Contains('\n'))
        {
            error = "ASS 스타일은 8192자 이하의 한 줄이어야 합니다.";
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
            error = $"ASS V4+ 스타일에는 정확히 {FieldCount}개 필드가 필요합니다.";
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
        SetRequiredText(NameIndex, _fields[NameIndex], "스타일 이름");
        SetRequiredText(FontNameIndex, _fields[FontNameIndex], "폰트 이름");

        ValidateFiniteRange(FontSize, 0, double.MaxValue, "폰트 크기", exclusiveMinimum: true);
        ValidateColour(PrimaryColour, "기본 색상");
        ValidateColour(SecondaryColour, "보조 색상");
        ValidateColour(OutlineColour, "외곽선 색상");
        ValidateColour(BackColour, "그림자 색상");

        _ = Bold;
        _ = Italic;
        _ = Underline;
        _ = StrikeOut;
        ValidateFiniteRange(ScaleX, 0, double.MaxValue, "가로 배율", exclusiveMinimum: true);
        ValidateFiniteRange(ScaleY, 0, double.MaxValue, "세로 배율", exclusiveMinimum: true);
        ValidateFiniteRange(Spacing, double.MinValue, double.MaxValue, "글자 간격");
        ValidateFiniteRange(Angle, double.MinValue, double.MaxValue, "회전 각도");

        if (BorderStyle is not (1 or 3))
        {
            throw new FormatException("ASS 테두리 형식은 1 또는 3이어야 합니다.");
        }

        ValidateFiniteRange(Outline, 0, double.MaxValue, "외곽선 두께");
        ValidateFiniteRange(Shadow, 0, double.MaxValue, "그림자 깊이");
        if (Alignment is < 1 or > 9)
        {
            throw new FormatException("ASS 정렬은 1부터 9까지여야 합니다.");
        }

        ValidateNonNegative(MarginLeft, "왼쪽 여백");
        ValidateNonNegative(MarginRight, "오른쪽 여백");
        ValidateNonNegative(MarginVertical, "수직 여백");
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
            throw new ArgumentException("ASS 필드에는 쉼표나 줄바꿈을 사용할 수 없습니다.", nameof(value));
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
            throw new ArgumentException($"ASS {label}이 올바르지 않습니다.", nameof(value));
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
            throw new FormatException($"ASS {label} 값이 올바른 숫자가 아닙니다.");
        }

        return result;
    }

    private static int ParseInteger(string value, string label)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException($"ASS {label} 값이 올바른 정수가 아닙니다.");
        }

        return result;
    }

    private static bool ParseAssBoolean(string value, string label)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            || result is not (-1 or 0 or 1))
        {
            throw new FormatException($"ASS {label} 값은 -1, 0 또는 1이어야 합니다.");
        }

        return result != 0;
    }

    private static void ValidateColour(string value, string label)
    {
        if (!ColourPattern.IsMatch(value))
        {
            throw new FormatException($"ASS {label}은 &H 뒤에 1~8자리 16진수로 입력해야 합니다.");
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
            var comparison = exclusiveMinimum ? "보다 커야" : "이상이어야";
            throw new ArgumentOutOfRangeException(nameof(value), $"ASS {label} 값은 {minimum} {comparison} 합니다.");
        }
    }

    private static void ValidateNonNegative(int value, string label)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"ASS {label} 값은 음수일 수 없습니다.");
        }
    }
}
