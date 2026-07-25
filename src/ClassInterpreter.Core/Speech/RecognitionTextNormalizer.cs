using System.Text.RegularExpressions;

namespace ClassInterpreter.Core.Speech;

public static partial class RecognitionTextNormalizer
{
    public static string Merge(string? confirmed, string? stash)
    {
        var stable = confirmed ?? string.Empty;
        var pending = stash ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stable)) return Sanitize(pending);
        if (string.IsNullOrWhiteSpace(pending)) return Sanitize(stable);

        var overlap = Math.Min(stable.Length, pending.Length);
        while (overlap > 0 && !stable.EndsWith(pending[..overlap], StringComparison.OrdinalIgnoreCase)) overlap--;
        return Sanitize(stable + pending[overlap..]);
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var value = text.Trim();
        value = Regex.Replace(value, "[ \\t]{2,}", " ");
        value = CollapseLeadingCommaLoop(value);
        value = CollapsePunctuatedLoops(value);
        value = RepeatedJoinedToken().Replace(value, "${unit}");
        value = RepeatedSeparatedToken().Replace(value, "${unit}");
        return value;
    }

    private static string CollapseLeadingCommaLoop(string value)
    {
        var hasTrailingSeparator = value.EndsWith('、') || value.EndsWith(',') || value.EndsWith('，');
        var pieces = Regex.Split(value, "[、,，]")
            .Select(piece => piece.Trim())
            .Where(piece => piece.Length > 0)
            .ToArray();
        if (pieces.Length < 6) return value;

        for (var period = 1; period <= Math.Min(4, pieces.Length / 3); period++)
        {
            var cycle = pieces[^period..];
            var repeatedStart = pieces.Length - period * 3;
            var repeated = true;
            for (var index = repeatedStart; index < pieces.Length; index++)
            {
                if (!string.Equals(pieces[index], cycle[(index - repeatedStart) % period], StringComparison.Ordinal))
                {
                    repeated = false;
                    break;
                }
            }
            if (!repeated) continue;

            while (repeatedStart >= period &&
                   pieces[(repeatedStart - period)..repeatedStart].SequenceEqual(cycle, StringComparer.Ordinal))
                repeatedStart -= period;

            var prefix = pieces[..repeatedStart];
            var truncatedCyclePrefix = prefix.Length == period && prefix
                .Select(piece => piece.TrimStart('.', '…', '・'))
                .Zip(cycle, (left, right) => right.EndsWith(left, StringComparison.Ordinal))
                .All(matches => matches);
            var resultPieces = prefix.Length == 0 || truncatedCyclePrefix ? cycle : prefix.Concat(cycle).ToArray();
            return string.Join("、", resultPieces) + (hasTrailingSeparator ? "、" : string.Empty);
        }
        return value;
    }

    private static string CollapsePunctuatedLoops(string value)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var match = RepeatedPunctuatedPhrase().Match(value);
            if (!match.Success) break;
            var unit = match.Groups["unit"].Value;
            var prefix = value[..match.Index];
            var trimmedPrefix = prefix.Trim().TrimStart('.', '…', '・');
            if (trimmedPrefix.Length > 0 && unit.EndsWith(trimmedPrefix, StringComparison.Ordinal)) prefix = string.Empty;
            value = prefix + unit + value[(match.Index + match.Length)..];
        }
        return value;
    }

    [GeneratedRegex(@"(?<unit>[\p{L}\p{N}]{2,12})(?:\k<unit>){2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedJoinedToken();

    [GeneratedRegex(@"\b(?<unit>[\p{L}\p{N}]{2,20})(?:[\s,，。.!！?？]+\k<unit>){2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedSeparatedToken();

    [GeneratedRegex(@"(?<unit>(?:[\p{L}\p{N}ー]+[、,，]\s*){2,4}?)(?:\k<unit>){2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedPunctuatedPhrase();
}
