using System.Text;
using ClassInterpreter.Core.Sessions;

namespace ClassInterpreter.Infrastructure.Timeline;

public static class TranscriptHistoryFormatter
{
    public static string Format(IReadOnlyList<TranscriptSegment> segments, bool sourceText, bool mergeByTime = true)
    {
        var finalSegments = segments
            .Where(segment => segment.IsFinal)
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.Sequence)
            .ToArray();
        if (finalSegments.Length == 0)
        {
            return "（没有字幕记录）";
        }

        var paragraphs = new List<string>();
        var buffer = new StringBuilder();
        var paragraphStart = TimeSpan.Zero;
        var previousEnd = TimeSpan.Zero;
        foreach (var segment in finalSegments)
        {
            var value = sourceText
                ? segment.SourceText
                : segment.TargetText ?? segment.ChineseText ?? string.Empty;
            value = Normalize(value);
            if (value.Length == 0)
            {
                continue;
            }

            if (!mergeByTime)
            {
                paragraphs.Add($"[{segment.Start:mm\\:ss}] {value}");
                continue;
            }

            if (buffer.Length > 0 && segment.Start - previousEnd > TimeSpan.FromSeconds(8))
            {
                paragraphs.Add($"[{paragraphStart:mm\\:ss}] {buffer}");
                buffer.Clear();
            }

            if (buffer.Length == 0)
            {
                paragraphStart = segment.Start;
            }
            else if (NeedsSpace(buffer[^1], value[0]))
            {
                buffer.Append(' ');
            }

            buffer.Append(value);
            previousEnd = segment.End > segment.Start ? segment.End : segment.Start;
            if ((EndsSentence(value) && buffer.Length >= 24) || buffer.Length >= 140)
            {
                paragraphs.Add($"[{paragraphStart:mm\\:ss}] {buffer}");
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            paragraphs.Add($"[{paragraphStart:mm\\:ss}] {buffer}");
        }

        return paragraphs.Count == 0 ? "（没有字幕记录）" : string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool EndsSentence(string text) => ".!?。！？；;：:".Contains(text[^1]);

    private static bool NeedsSpace(char previous, char next) =>
        (char.IsLetterOrDigit(previous) && previous <= 127) ||
        (char.IsLetterOrDigit(next) && next <= 127);
}
