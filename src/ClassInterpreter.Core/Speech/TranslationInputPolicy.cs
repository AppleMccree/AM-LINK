namespace ClassInterpreter.Core.Speech;

public static class TranslationInputPolicy
{
    public static bool ShouldTranslate(TranslationDirection direction, string? recognizedLanguage, string text)
    {
        if (direction.TargetLanguage == "Chinese")
        {
            if (direction == TranslationDirection.MixedToChinese) return true;
            var expected = direction.SourceLanguage == "Japanese" ? "ja" : "en";
            return recognizedLanguage?.StartsWith(expected, StringComparison.OrdinalIgnoreCase) == true
                || string.Equals(recognizedLanguage, direction.SourceLanguage, StringComparison.OrdinalIgnoreCase);
        }

        if (recognizedLanguage?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(recognizedLanguage, "Chinese", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.Any(character => character is >= '\u4e00' and <= '\u9fff');
    }
}
