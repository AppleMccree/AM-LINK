namespace ClassInterpreter.Core.Slides;

public sealed record SlideDocument(string SourcePath, IReadOnlyList<SlidePage> Pages);

public sealed record SlidePage(
    int PageNumber,
    string Title,
    string Text,
    string Notes,
    string? ThumbnailPath = null,
    string? VisualDescription = null);
