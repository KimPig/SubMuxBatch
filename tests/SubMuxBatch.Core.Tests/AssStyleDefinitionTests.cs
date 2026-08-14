using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.Core.Tests;

public sealed class AssStyleDefinitionTests
{
    private const string StyleBody =
        "Default,Test Font,79.5,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,95,0.0,1.25,1,2.3,3.8,2,30,31,77,1";

    [Theory]
    [InlineData("Style: " + StyleBody)]
    [InlineData(StyleBody)]
    [InlineData("style: " + StyleBody)]
    public void AcceptsStyleWithOrWithoutPrefix(string input)
    {
        var style = AssStyleDefinition.Parse(input);

        Assert.Equal("Default", style.Name);
        Assert.Equal("Test Font", style.FontName);
        Assert.Equal(79.5, style.FontSize);
        Assert.Equal("Style: " + StyleBody, style.ToStyleLine());
    }

    [Fact]
    public void TrimsWhitespaceAroundCommasAndWritesCanonicalLine()
    {
        var input = "  Style:  " + StyleBody.Replace(",", " , ", StringComparison.Ordinal) + "  ";

        var style = AssStyleDefinition.Parse(input);

        Assert.Equal("Style: " + StyleBody, style.ToStyleLine());
    }

    [Fact]
    public void ExposesMajorFieldsAndPreservesUneditedFields()
    {
        var style = AssStyleDefinition.Parse(StyleBody);
        var originalSecondary = style.SecondaryColour;
        var originalScaleX = style.ScaleX;
        var originalScaleY = style.ScaleY;
        var originalSpacing = style.Spacing;
        var originalAngle = style.Angle;
        var originalBorderStyle = style.BorderStyle;
        var originalEncoding = style.Encoding;

        style.FontName = "New Font";
        style.FontSize = 64.25;
        style.PrimaryColour = "&H00ABCDEF";
        style.OutlineColour = "&H00112233";
        style.BackColour = "&H80123456";
        style.Bold = false;
        style.Italic = true;
        style.Outline = 1.75;
        style.Shadow = 2.25;
        style.Alignment = 8;
        style.MarginLeft = 40;
        style.MarginRight = 41;
        style.MarginVertical = 90;

        var reparsed = AssStyleDefinition.Parse(style.ToStyleLine());

        Assert.Equal("New Font", reparsed.FontName);
        Assert.Equal(64.25, reparsed.FontSize);
        Assert.Equal("&H00ABCDEF", reparsed.PrimaryColour);
        Assert.Equal("&H00112233", reparsed.OutlineColour);
        Assert.Equal("&H80123456", reparsed.BackColour);
        Assert.False(reparsed.Bold);
        Assert.True(reparsed.Italic);
        Assert.Equal(1.75, reparsed.Outline);
        Assert.Equal(2.25, reparsed.Shadow);
        Assert.Equal(8, reparsed.Alignment);
        Assert.Equal(40, reparsed.MarginLeft);
        Assert.Equal(41, reparsed.MarginRight);
        Assert.Equal(90, reparsed.MarginVertical);
        Assert.Equal(originalSecondary, reparsed.SecondaryColour);
        Assert.Equal(originalScaleX, reparsed.ScaleX);
        Assert.Equal(originalScaleY, reparsed.ScaleY);
        Assert.Equal(originalSpacing, reparsed.Spacing);
        Assert.Equal(originalAngle, reparsed.Angle);
        Assert.Equal(originalBorderStyle, reparsed.BorderStyle);
        Assert.Equal(originalEncoding, reparsed.Encoding);
    }

    [Fact]
    public void FieldsReturnsAllValuesWithoutAllowingArrayMutation()
    {
        var style = AssStyleDefinition.Parse(StyleBody);
        var fields = style.Fields;

        Assert.Equal(AssStyleDefinition.FieldCount, fields.Count);
        Assert.Equal("Default", fields[0]);
        Assert.Equal("1", fields[^1]);
        Assert.False(fields is string[]);
    }

    [Fact]
    public void SetFieldCanEditAHiddenFieldAndRollsBackInvalidChanges()
    {
        var style = AssStyleDefinition.Parse(StyleBody);

        style.SetField(4, "&H00ABCDEF");
        Assert.Equal("&H00ABCDEF", style.SecondaryColour);

        Assert.Throws<FormatException>(() => style.SetField(4, "not-a-colour"));
        Assert.Equal("&H00ABCDEF", style.SecondaryColour);
        Assert.Throws<ArgumentOutOfRangeException>(() => style.SetField(AssStyleDefinition.FieldCount, "0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Style: Default,Arial,20")]
    [InlineData("Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,100,0,0,1,2,3,2,10,10,10")]
    [InlineData("Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,100,0,0,1,2,3,2,10,10,10,1,extra")]
    [InlineData("Style: Default,Arial,20\n,H00FFFFFF,&H000000FF,&H00000000,&H64000000,-1,0,0,0,100,100,0,0,1,2,3,2,10,10,10,1")]
    public void RejectsMissingExtraOrMultilineFields(string input)
    {
        Assert.False(AssStyleDefinition.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Throws<FormatException>(() => AssStyleDefinition.Parse(input));
    }

    [Theory]
    [InlineData("Field", "not-a-number")]
    [InlineData("Colour", "FFFFFF")]
    [InlineData("Colour", "&HGGFFFFFF")]
    [InlineData("Boolean", "2")]
    [InlineData("Border", "2")]
    [InlineData("Alignment", "0")]
    [InlineData("Alignment", "10")]
    [InlineData("Margin", "-1")]
    public void RejectsInvalidTypedFields(string field, string invalidValue)
    {
        var values = StyleBody.Split(',');
        values[field switch
        {
            "Field" => 2,
            "Colour" => 3,
            "Boolean" => 7,
            "Border" => 15,
            "Alignment" => 18,
            "Margin" => 19,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        }] = invalidValue;

        Assert.False(AssStyleDefinition.TryParse(string.Join(',', values), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RejectsEmptyRequiredNamesAndInvalidRanges()
    {
        var emptyName = StyleBody.Split(',');
        emptyName[0] = "";
        var emptyFont = StyleBody.Split(',');
        emptyFont[1] = "";
        var negativeOutline = StyleBody.Split(',');
        negativeOutline[16] = "-0.1";
        var zeroScale = StyleBody.Split(',');
        zeroScale[11] = "0";

        Assert.False(AssStyleDefinition.TryParse(string.Join(',', emptyName), out _));
        Assert.False(AssStyleDefinition.TryParse(string.Join(',', emptyFont), out _));
        Assert.False(AssStyleDefinition.TryParse(string.Join(',', negativeOutline), out _));
        Assert.False(AssStyleDefinition.TryParse(string.Join(',', zeroScale), out _));
    }

    [Fact]
    public void SetterValidationPreventsInvalidEditableValues()
    {
        var style = AssStyleDefinition.Parse(StyleBody);

        Assert.Throws<ArgumentException>(() => style.FontName = "Bad,Font");
        Assert.Throws<ArgumentOutOfRangeException>(() => style.FontSize = 0);
        Assert.Throws<FormatException>(() => style.PrimaryColour = "white");
        Assert.Throws<ArgumentOutOfRangeException>(() => style.Outline = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => style.Alignment = 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => style.MarginVertical = -1);
    }
}
