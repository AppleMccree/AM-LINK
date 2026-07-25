using ClassInterpreter.Core.Slides;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;

namespace ClassInterpreter.Infrastructure.Slides;

public sealed class PptxSlideExtractor
{
    public SlideDocument Extract(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        using var presentation = PresentationDocument.Open(fullPath, false);
        var presentationPart = presentation.PresentationPart
            ?? throw new InvalidDataException("PPTX 缺少 PresentationPart。");
        var presentationRoot = presentationPart.Presentation
            ?? throw new InvalidDataException("PPTX 缺少 Presentation 根节点。");
        var slideIds = presentationRoot.SlideIdList?.ChildElements
            .OfType<DocumentFormat.OpenXml.Presentation.SlideId>()
            .ToArray() ?? [];
        var pages = new List<SlidePage>(slideIds.Length);

        for (var index = 0; index < slideIds.Length; index++)
        {
            var relationshipId = slideIds[index].RelationshipId?.Value
                ?? throw new InvalidDataException($"第 {index + 1} 页缺少关系 ID。");
            var slidePart = (SlidePart)presentationPart.GetPartById(relationshipId);
            var slide = slidePart.Slide
                ?? throw new InvalidDataException($"第 {index + 1} 页缺少 Slide 根节点。");
            var texts = slide.Descendants<A.Text>()
                .Select(text => text.Text?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Cast<string>()
                .ToArray();
            var notesSlide = slidePart.NotesSlidePart?.NotesSlide;
            var notes = notesSlide?.Descendants<A.Text>()
                .Select(text => text.Text?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Cast<string>()
                .ToArray() ?? [];
            pages.Add(new SlidePage(
                index + 1,
                texts.FirstOrDefault() ?? $"第 {index + 1} 页",
                string.Join(" ", texts),
                string.Join(" ", notes)));
        }

        return new SlideDocument(fullPath, pages);
    }
}
