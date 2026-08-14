using System.Globalization;
using System.Text.RegularExpressions;

namespace SubMuxBatch.Core.External;

/// <summary>
/// Restores the small subset of ASS positioning overrides that Subtitle Edit removes
/// while converting SRT. Existing inline values and dialogue margins are preserved.
/// This is intended for SRT -&gt; ASS output only.
/// </summary>
public static class AssInlineStylePostProcessor
{
    private const string NumberPattern = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)";

    private static readonly Regex DialoguePrefixPattern = new(
        @"^Dialogue\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AssTimePattern = new(
        @"^(?<h>\d+):(?<m>\d{2}):(?<s>\d{2})[.](?<cs>\d{2})$",
        RegexOptions.Compiled);

    private static readonly Regex SrtTimePattern = new(
        @"(?<sh>\d{1,2}):(?<sm>\d{2}):(?<ss>\d{2})[,.](?<sms>\d{3})\s*-->\s*" +
        @"(?<eh>\d{1,2}):(?<em>\d{2}):(?<es>\d{2})[,.](?<ems>\d{3})",
        RegexOptions.Compiled);

    private static readonly Regex OverrideBlockPattern = new(
        @"\{(?<body>[^{}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex RestorableTagPattern = new(
        $@"\\an[1-9]|\\(?:pos|org)\(\s*{NumberPattern}\s*,\s*{NumberPattern}\s*\)|" +
        $@"\\move\(\s*{NumberPattern}\s*,\s*{NumberPattern}\s*,\s*{NumberPattern}\s*,\s*{NumberPattern}" +
        $@"(?:\s*,\s*{NumberPattern}\s*,\s*{NumberPattern})?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Apply(
        string convertedAss,
        string originalSrt)
    {
        ArgumentNullException.ThrowIfNull(convertedAss);
        ArgumentNullException.ThrowIfNull(originalSrt);

        var sourceCues = ParseSrtCues(originalSrt);
        var newLine = DetectNewLine(convertedAss);
        var lines = Regex.Split(convertedAss, @"\r\n|\n|\r");

        for (var index = 0; index < lines.Length; index++)
        {
            if (!DialoguePrefixPattern.IsMatch(lines[index]))
            {
                continue;
            }

            lines[index] = ProcessDialogue(lines[index], sourceCues);
        }

        return string.Join(newLine, lines);
    }

    private static string ProcessDialogue(
        string line,
        IReadOnlyDictionary<CueKey, Queue<string>> sourceCues)
    {
        var colon = line.IndexOf(':');
        var contentStart = colon + 1;
        while (contentStart < line.Length && char.IsWhiteSpace(line[contentStart]))
        {
            contentStart++;
        }

        var prefix = line[..contentStart];
        var fields = SplitDialogueFields(line[contentStart..]);
        if (fields is null)
        {
            return line;
        }

        if (TryParseAssTime(fields[1], out var start)
            && TryParseAssTime(fields[2], out var end)
            && TryTakeSourceTags(sourceCues, new CueKey(start, end), out var sourceTags))
        {
            var missingTags = FilterMissingTags(fields[9], sourceTags);
            fields[9] = InjectTags(fields[9], missingTags);
        }

        return prefix + string.Join(',', fields);
    }

    private static string[]? SplitDialogueFields(string content)
    {
        var fields = new string[10];
        var fieldStart = 0;
        for (var index = 0; index < 9; index++)
        {
            var comma = content.IndexOf(',', fieldStart);
            if (comma < 0)
            {
                return null;
            }

            fields[index] = content[fieldStart..comma];
            fieldStart = comma + 1;
        }

        fields[9] = content[fieldStart..];
        return fields;
    }

    private static string FilterMissingTags(string dialogueText, string sourceTags)
    {
        var missing = new List<string>();
        foreach (Match match in RestorableTagPattern.Matches(sourceTags))
        {
            var command = GetTagCommand(match.Value);
            if (!ContainsTagCommand(dialogueText, command))
            {
                missing.Add(match.Value);
            }
        }

        return string.Concat(missing);
    }

    private static string GetTagCommand(string tag)
    {
        var end = 1;
        while (end < tag.Length && char.IsLetter(tag[end]))
        {
            end++;
        }

        return tag[1..end].ToLowerInvariant();
    }

    private static bool ContainsTagCommand(string text, string command) =>
        Regex.IsMatch(text, $@"\\{Regex.Escape(command)}(?=\d|\s|\()", RegexOptions.IgnoreCase);

    private static string InjectTags(string text, string tags)
    {
        if (string.IsNullOrEmpty(tags))
        {
            return text;
        }

        if (text.StartsWith('{'))
        {
            var end = text.IndexOf('}');
            if (end > 0)
            {
                return text.Insert(end, tags);
            }
        }

        return $"{{{tags}}}{text}";
    }

    private static IReadOnlyDictionary<CueKey, Queue<string>> ParseSrtCues(string srt)
    {
        var cues = new Dictionary<CueKey, Queue<string>>();
        var normalized = srt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = Regex.Split(normalized.Trim(), @"\n[\t ]*\n");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timeIndex = Array.FindIndex(lines, static line => SrtTimePattern.IsMatch(line));
            if (timeIndex < 0)
            {
                continue;
            }

            var timeMatch = SrtTimePattern.Match(lines[timeIndex]);
            var start = ToCentiseconds(timeMatch, "sh", "sm", "ss", "sms");
            var end = ToCentiseconds(timeMatch, "eh", "em", "es", "ems");
            var text = string.Join('\n', lines.Skip(timeIndex + 1));
            var tags = string.Concat(
                OverrideBlockPattern.Matches(text)
                    .SelectMany(static blockMatch => RestorableTagPattern.Matches(blockMatch.Groups["body"].Value))
                    .Select(static tagMatch => tagMatch.Value));
            if (tags.Length == 0)
            {
                continue;
            }

            var key = new CueKey(start, end);
            if (!cues.TryGetValue(key, out var queue))
            {
                queue = new Queue<string>();
                cues.Add(key, queue);
            }

            queue.Enqueue(tags);
        }

        return cues;
    }

    private static bool TryTakeSourceTags(
        IReadOnlyDictionary<CueKey, Queue<string>> cues,
        CueKey target,
        out string tags)
    {
        // seconv writes centiseconds. Depending on the millisecond input it may round
        // either side of the boundary, so accept at most one centisecond of drift.
        for (var distance = 0; distance <= 2; distance++)
        {
            for (var startOffset = -1; startOffset <= 1; startOffset++)
            {
                for (var endOffset = -1; endOffset <= 1; endOffset++)
                {
                    if (Math.Abs(startOffset) + Math.Abs(endOffset) != distance)
                    {
                        continue;
                    }

                    var key = new CueKey(target.StartCentiseconds + startOffset, target.EndCentiseconds + endOffset);
                    if (cues.TryGetValue(key, out var queue) && queue.Count > 0)
                    {
                        tags = queue.Dequeue();
                        return true;
                    }
                }
            }
        }

        tags = string.Empty;
        return false;
    }

    private static bool TryParseAssTime(string value, out long centiseconds)
    {
        var match = AssTimePattern.Match(value.Trim());
        if (!match.Success)
        {
            centiseconds = 0;
            return false;
        }

        centiseconds = ((long)Parse(match, "h") * 3600
                       + Parse(match, "m") * 60
                       + Parse(match, "s")) * 100
                       + Parse(match, "cs");
        return true;
    }

    private static long ToCentiseconds(Match match, string hours, string minutes, string seconds, string milliseconds)
    {
        var totalMilliseconds = ((long)Parse(match, hours) * 3600
                                + Parse(match, minutes) * 60
                                + Parse(match, seconds)) * 1000
                                + Parse(match, milliseconds);
        return (long)Math.Round(totalMilliseconds / 10d, MidpointRounding.AwayFromZero);
    }

    private static int Parse(Match match, string group) =>
        int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string DetectNewLine(string value) =>
        value.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
        : value.Contains('\r') ? "\r"
        : "\n";

    private readonly record struct CueKey(long StartCentiseconds, long EndCentiseconds);
}
