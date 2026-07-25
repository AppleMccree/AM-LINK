namespace ClassInterpreter.Core.Slides;

public enum SlideFollowMode
{
    Manual,
    Suggest,
    Automatic
}

public enum SlideFollowAction
{
    None,
    Suggested,
    Accepted,
    Ignored,
    AutoFollowed,
    ManuallyViewed
}

public sealed record SlideLink(
    int? ViewedPage,
    int? CandidatePage,
    double? Confidence,
    string? Evidence,
    SlideFollowAction Action);
