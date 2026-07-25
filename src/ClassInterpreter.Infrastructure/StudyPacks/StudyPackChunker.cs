namespace ClassInterpreter.Infrastructure.StudyPacks;

public static class StudyPackChunker
{
    public static IReadOnlyList<string> Split(string text, int chunkSize = 40_000, int overlap = 800)
    {
        if (string.IsNullOrEmpty(text)) return [string.Empty];
        if (chunkSize <= overlap || overlap < 0) throw new ArgumentOutOfRangeException(nameof(overlap));
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(chunkSize, text.Length - start);
            var end = start + length;
            if (end < text.Length)
            {
                var newline = text.LastIndexOf('\n', end - 1, Math.Min(length, 1200));
                if (newline > start + chunkSize / 2) end = newline + 1;
            }
            chunks.Add(text[start..end]);
            if (end >= text.Length) break;
            start = Math.Max(start + 1, end - overlap);
        }
        return chunks;
    }
}
