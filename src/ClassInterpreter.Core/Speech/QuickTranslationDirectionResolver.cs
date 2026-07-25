using System.Text.RegularExpressions;

namespace ClassInterpreter.Core.Speech;

public static class QuickTranslationDirectionResolver
{
    public static TranslationDirection Resolve(string text, string foreignLanguage)
    {
        if (string.Equals(foreignLanguage, "日文", StringComparison.Ordinal))
        {
            var containsKana = Regex.IsMatch(text, "[\\p{IsHiragana}\\p{IsKatakana}]");
            return containsKana ? TranslationDirection.JapaneseToChinese : TranslationDirection.ChineseToJapanese;
        }
        var containsChinese = Regex.IsMatch(text, "[\\p{IsCJKUnifiedIdeographs}]");
        return containsChinese ? TranslationDirection.ChineseToEnglish : TranslationDirection.EnglishToChinese;
    }
}
