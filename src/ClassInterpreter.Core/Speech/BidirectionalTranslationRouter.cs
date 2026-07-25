using System.Text.RegularExpressions;

namespace ClassInterpreter.Core.Speech;

public static class BidirectionalTranslationRouter
{
    public static TranslationDirection? Resolve(
        TranslationDirection selected,
        string? recognizedLanguage,
        string text)
    {
        if (!selected.IsBidirectional)
            return TranslationInputPolicy.ShouldTranslate(selected, recognizedLanguage, text) ? selected : null;

        var language = recognizedLanguage ?? string.Empty;
        var isChinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                        || language.Equals("Chinese", StringComparison.OrdinalIgnoreCase);
        var hasKana = Regex.IsMatch(text, "[\\p{IsHiragana}\\p{IsKatakana}]");
        var hasHan = Regex.IsMatch(text, "[\\p{IsCJKUnifiedIdeographs}]");
        var hasChineseEvidence = Regex.IsMatch(text, "[这为么们说话门听问让还进过时个样请吗呢吧哪给跟从对没很把被着了的]");
        var isShortBackchannel = IsShortBackchannel(text);

        if (selected == TranslationDirection.JapaneseChineseBidirectional)
        {
            if (isShortBackchannel) return null;
            // A single mono stream can contain two people at once. Never force a mixed/conflicting
            // segment into one side; the UI keeps it pending instead of contaminating either pane.
            if (hasKana && hasChineseEvidence) return null;
            if (hasKana)
                return TranslationDirection.JapaneseToChinese;
            if (hasChineseEvidence)
                return TranslationDirection.ChineseToJapanese;
            if (language.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                return TranslationDirection.JapaneseToChinese;
            if (isChinese)
                return TranslationDirection.ChineseToJapanese;
            // Han-only short phrases are genuinely ambiguous between Chinese and Japanese.
            // Waiting for a language label is safer than putting the utterance in the wrong pane.
            if (hasHan) return null;
            return null;
        }

        if (selected == TranslationDirection.EnglishChineseBidirectional)
        {
            if (isShortBackchannel) return null;
            var hasLatin = Regex.IsMatch(text, "[A-Za-z]");
            if (hasLatin && hasHan) return null;
            if (hasHan)
                return TranslationDirection.ChineseToEnglish;
            if (hasLatin)
                return TranslationDirection.EnglishToChinese;
            if (isChinese) return TranslationDirection.ChineseToEnglish;
            if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return TranslationDirection.EnglishToChinese;
        }

        return null;
    }

    public static bool HasStrongEvidence(TranslationDirection route, string? recognizedLanguage, string text)
    {
        var language = recognizedLanguage ?? string.Empty;
        if (route == TranslationDirection.JapaneseToChinese)
            return Regex.IsMatch(text, "[\\p{IsHiragana}\\p{IsKatakana}]") && !Regex.IsMatch(text, "[这为么们说话听问让还请吗呢吧哪给从对没很把被着]");
        if (route == TranslationDirection.ChineseToJapanese || route == TranslationDirection.ChineseToEnglish)
            return Regex.IsMatch(text, "[这为么们说话听问让还请吗呢吧哪给从对没很把被着]")
                   || language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && text.Length >= 6;
        if (route == TranslationDirection.EnglishToChinese)
            return Regex.Matches(text, "[A-Za-z]").Count >= 4;
        return false;
    }

    public static bool ContainsKana(string text) =>
        Regex.IsMatch(text, "[\\p{IsHiragana}\\p{IsKatakana}]");

    public static bool ContainsLatin(string text) => Regex.IsMatch(text, "[A-Za-z]");

    public static bool ContainsStrongChineseEvidence(string text) => Regex.IsMatch(
        text,
        "[\u6211\u4f60\u4ed6\u5979\u5b83\u4eec\u8fd9\u90a3\u4e3a\u4ec0\u4e48\u600e\u4e48\u8bf4\u8bdd\u542c\u95ee\u8ba9\u8fd8\u8bf7\u5417\u5462\u5427\u7ed9\u4ece\u5bf9\u6ca1\u5f88\u628a\u88ab\u7740\u4e86\u7684\u4f1a\u8981\u5c31\u4e5f\u90fd\u5728\u6709\u548c\u4e0d\u4eba\u6765\u53bb\u53ef\u4ee5\u8001\u5e08\u95ee\u9898\u73b0\u5728]");

    public static bool IsShortBackchannel(string text)
    {
        var value = Regex.Replace(text.Trim().ToLowerInvariant(), "[\\s、,，。.!！?？…]+", string.Empty);
        return value.Length <= 8 && Regex.IsMatch(value,
            "^(嗯+|啊+|哦+|对+|是+|好+|はい+|うん+|ええ+|そう+|なるほど|ok+|okay+|yes+|yeah+|uh+|um+|hm+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
