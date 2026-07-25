using System.Text.RegularExpressions;

namespace ClassInterpreter.Core.Slides;

public static partial class SlideTerminology
{
    public static IReadOnlyList<string> Extract(SlideDocument? document, int currentPage, int maximum = 40)
    {
        if (document is null || maximum <= 0) return [];
        var pages = document.Pages
            .Where(page => Math.Abs(page.PageNumber - currentPage) <= 1)
            .OrderBy(page => Math.Abs(page.PageNumber - currentPage));
        var terms = new List<string>();
        foreach (var page in pages)
        {
            var text = $"{page.Title}\n{page.Text}\n{page.Notes}";
            foreach (Match match in CandidateRegex().Matches(text))
            {
                var term = match.Value.Trim(' ', '\t', '\r', '\n', '・', '-', '–', '—');
                if (term.Length < 2 || term.Length > 80 || terms.Contains(term, StringComparer.OrdinalIgnoreCase)) continue;
                terms.Add(term);
                if (terms.Count == maximum) return terms;
            }
        }
        return terms;
    }

    public static string BuildDomainHint(SlideDocument? document, int currentPage)
    {
        var terms = Extract(document, currentPage);
        return terms.Count == 0
            ? string.Empty
            : "This is a university lecture. Use the following terminology from the current and adjacent PPT pages " +
              "to resolve ambiguous speech-recognition words and keep technical names consistent: " + string.Join("; ", terms);
    }

    public static IReadOnlyList<string> PreservedTerms(SlideDocument? document, int currentPage) =>
        Extract(document, currentPage)
            .Where(term => term.Any(char.IsLetter) && term.All(ch => ch < 128))
            .Take(20)
            .ToArray();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.#/'’-]*(?:[ -][A-Za-z0-9+.#/'’-]+){0,5}|[\p{IsKatakana}ー]{3,}|[\p{IsCJKUnifiedIdeographs}々]{2,10}")]
    private static partial Regex CandidateRegex();
}
