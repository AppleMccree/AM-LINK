using ClassInterpreter.Core.Slides;
using UglyToad.PdfPig;

namespace ClassInterpreter.Infrastructure.Slides;

public sealed class PdfSlideExtractor
{
    public SlideDocument Extract(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        using var document = PdfDocument.Open(fullPath);
        var pages = new List<SlidePage>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            var text = page.Text?.Trim() ?? string.Empty;
            var title = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? $"第 {page.Number} 页";
            pages.Add(new SlidePage(page.Number, title, text, string.Empty));
        }

        return new SlideDocument(fullPath, pages);
    }
}
