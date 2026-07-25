using ClassInterpreter.Core.Slides;

namespace ClassInterpreter.Infrastructure.Slides;

public enum SlideFollowDecisionKind
{
    None,
    Suggest,
    AutoNavigate
}

public sealed record SlideFollowDecision(
    SlideFollowDecisionKind Kind,
    SlideCandidate? Candidate,
    string Status);

/// <summary>
/// Guards slide matching so a lexical candidate can never immediately hijack a student's reading.
/// A candidate must be stable over finalized subtitles; a manual navigation pauses every follow mode.
/// </summary>
public sealed class SlideFollowController
{
    private readonly List<int> _recentCandidatePages = [];
    private DateTimeOffset _lastNavigationAt = DateTimeOffset.MinValue;
    private int? _lastSuggestedPage;

    public SlideFollowMode Mode { get; private set; } = SlideFollowMode.Manual;
    public bool IsPausedByStudent { get; private set; }

    public void SetMode(SlideFollowMode mode)
    {
        Mode = mode;
        IsPausedByStudent = false;
        _recentCandidatePages.Clear();
        _lastSuggestedPage = null;
    }

    public void PauseForManualNavigation()
    {
        IsPausedByStudent = true;
        _recentCandidatePages.Clear();
        _lastSuggestedPage = null;
    }

    public void Resume()
    {
        IsPausedByStudent = false;
        _recentCandidatePages.Clear();
        _lastSuggestedPage = null;
    }

    public SlideFollowDecision Evaluate(SlideMatchResult result, int currentPage, DateTimeOffset now)
    {
        var candidate = result.Candidates.FirstOrDefault();
        if (Mode == SlideFollowMode.Manual)
            return new(SlideFollowDecisionKind.None, candidate, "手动浏览：后台仅建立字幕与课件关联");
        if (IsPausedByStudent)
            return new(SlideFollowDecisionKind.None, candidate, "你正在手动浏览，跟随已暂停");

        if (candidate is null || !HasPromptEvidence(candidate, result))
            return new(SlideFollowDecisionKind.None, candidate, "课件关联证据不足，未提示跳转");

        _recentCandidatePages.Add(candidate.PageNumber);
        if (_recentCandidatePages.Count > 3) _recentCandidatePages.RemoveAt(0);
        var stable = _recentCandidatePages.Count(page => page == candidate.PageNumber) >= 2;
        if (!stable)
            return new(SlideFollowDecisionKind.None, candidate, $"正在确认可能的第 {candidate.PageNumber} 页……");

        var distance = Math.Abs(candidate.PageNumber - currentPage);
        if (Mode == SlideFollowMode.Automatic
            && HasAutoEvidence(candidate, result)
            && distance <= 3
            && now - _lastNavigationAt >= TimeSpan.FromSeconds(12))
        {
            _lastNavigationAt = now;
            _lastSuggestedPage = candidate.PageNumber;
            return new(SlideFollowDecisionKind.AutoNavigate, candidate,
                $"已稳定匹配第 {candidate.PageNumber} 页，正在跟随课件");
        }

        if (_lastSuggestedPage == candidate.PageNumber)
            return new(SlideFollowDecisionKind.None, candidate, $"仍可能正在讲第 {candidate.PageNumber} 页");

        _lastSuggestedPage = candidate.PageNumber;
        return new(SlideFollowDecisionKind.Suggest, candidate,
            distance > 3
                ? $"可能正在讲第 {candidate.PageNumber} 页（距离较远，请确认）"
                : $"可能正在讲第 {candidate.PageNumber} 页");
    }

    public void RecordManualOrAcceptedNavigation(DateTimeOffset now)
    {
        _lastNavigationAt = now;
        _lastSuggestedPage = null;
    }

    private static bool HasPromptEvidence(SlideCandidate candidate, SlideMatchResult result)
    {
        var runnerUp = result.Candidates.Skip(1).FirstOrDefault()?.Score ?? 0d;
        return candidate.EvidenceTerms.Count >= 2
               && candidate.Score >= 0.42
               && candidate.Score - runnerUp >= 0.08;
    }

    private static bool HasAutoEvidence(SlideCandidate candidate, SlideMatchResult result)
    {
        var runnerUp = result.Candidates.Skip(1).FirstOrDefault()?.Score ?? 0d;
        return candidate.AutoFocusAllowed
               && candidate.EvidenceTerms.Count >= 3
               && candidate.Score >= 0.62
               && candidate.Score - runnerUp >= 0.14;
    }
}
