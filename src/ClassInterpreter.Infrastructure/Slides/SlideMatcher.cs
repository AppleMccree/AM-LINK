using System.Text.RegularExpressions;
using ClassInterpreter.Core.Slides;

namespace ClassInterpreter.Infrastructure.Slides;

public sealed record SlideMatchContext(
    SlideDocument Document,
    int CurrentPage,
    IReadOnlyList<string> StableTranscriptWindow);

public sealed record SlideCandidate(
    int PageNumber,
    double Score,
    IReadOnlyList<string> EvidenceTerms,
    bool AutoFocusAllowed);

public sealed record SlideMatchResult(IReadOnlyList<SlideCandidate> Candidates);

public sealed partial class SlideMatcher
{
    public SlideMatchResult Match(SlideMatchContext context)
    {
        var transcriptTerms = BuildTranscriptTerms(context.StableTranscriptWindow);
        var candidates = context.Document.Pages
            .Select(page => Score(page, context.CurrentPage, transcriptTerms))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => DirectionRank(candidate.PageNumber, context.CurrentPage))
            .Take(3)
            .ToArray();
        return new SlideMatchResult(candidates);
    }

    private static SlideCandidate Score(
        SlidePage page,
        int currentPage,
        IReadOnlyDictionary<string, double> transcriptTerms)
    {
        var pageTerms = BuildPageTerms(page);
        var evidence = transcriptTerms.Keys
            .Where(pageTerms.ContainsKey)
            .OrderByDescending(term => transcriptTerms[term] * pageTerms[term])
            .ToArray();

        var transcriptWeight = transcriptTerms.Values.Sum();
        var matchedWeight = evidence.Sum(term => transcriptTerms[term]);
        var transcriptCoverage = transcriptWeight <= 0 ? 0d : matchedWeight / transcriptWeight;

        // Several distinct matches are much safer than one common word. Titles receive extra
        // weight so a teacher announcing a new section can turn to that slide promptly.
        var evidenceStrength = evidence.Sum(term => Math.Min(2.5, pageTerms[term]));
        var evidenceScore = Math.Min(0.28, evidenceStrength * 0.045);
        var lexical = transcriptCoverage * 0.72 + evidenceScore;

        var delta = page.PageNumber - currentPage;
        var proximity = delta switch
        {
            0 => 0.07,
            1 => 0.12,
            -1 => 0.06,
            2 => 0.035,
            _ => -Math.Min(0.24, Math.Max(0, Math.Abs(delta) - 2) * 0.055)
        };
        var score = Math.Clamp(lexical + proximity, 0d, 1d);

        var minimumEvidence = Math.Abs(delta) <= 1 ? 2 : 3;
        var minimumScore = Math.Abs(delta) <= 1 ? 0.18 : 0.28;
        var autoFocus = evidence.Length >= minimumEvidence && score >= minimumScore;
        return new SlideCandidate(page.PageNumber, score, evidence, autoFocus);
    }

    private static Dictionary<string, double> BuildTranscriptTerms(IReadOnlyList<string> window)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < window.Count; index++)
        {
            // The latest finalized sentence is the strongest signal; older sentences provide
            // context without holding the viewer on the previous slide.
            var recency = 0.35 + 0.65 * (index + 1d) / window.Count;
            foreach (var term in Tokenize(window[index]))
            {
                result[term] = Math.Max(result.GetValueOrDefault(term), recency);
            }
        }

        return result;
    }

    private static Dictionary<string, double> BuildPageTerms(SlidePage page)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        AddTerms(result, page.Title, 2.5);
        AddTerms(result, page.Text, 1.0);
        AddTerms(result, page.Notes, 0.8);
        AddTerms(result, page.VisualDescription ?? string.Empty, 0.8);
        return result;
    }

    private static void AddTerms(Dictionary<string, double> destination, string text, double weight)
    {
        foreach (var term in Tokenize(text))
        {
            destination[term] = Math.Max(destination.GetValueOrDefault(term), weight);
        }
    }

    private static HashSet<string> Tokenize(string text)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in LatinWord().Matches(text.ToLowerInvariant()))
        {
            var word = match.Value.Trim();
            if (word.Length >= 2 || word is "q" or "k" or "v")
            {
                terms.Add(word);
            }
        }

        foreach (Match match in CjkRun().Matches(text))
        {
            var run = match.Value.Trim();
            if (run.Length <= 8)
            {
                terms.Add(run);
            }

            // Character n-grams work for Chinese and Japanese without requiring a language-
            // specific word segmenter, while avoiding the false confidence caused by single chars.
            for (var size = 2; size <= Math.Min(3, run.Length); size++)
            {
                for (var index = 0; index <= run.Length - size; index++)
                {
                    terms.Add(run.Substring(index, size));
                }
            }
        }

        return terms;
    }

    private static int DirectionRank(int page, int currentPage)
    {
        var delta = page - currentPage;
        return delta switch
        {
            0 => 0,
            1 => 1,
            -1 => 2,
            > 1 => 3 + delta,
            _ => 100 + Math.Abs(delta)
        };
    }

    [GeneratedRegex("[a-z0-9]+(?:[-_][a-z0-9]+)*")]
    private static partial Regex LatinWord();

    [GeneratedRegex("[\\p{IsCJKUnifiedIdeographs}\\p{IsHiragana}\\p{IsKatakana}]+")]
    private static partial Regex CjkRun();
}
