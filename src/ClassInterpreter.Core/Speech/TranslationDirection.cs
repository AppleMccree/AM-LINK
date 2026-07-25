namespace ClassInterpreter.Core.Speech;

public sealed record TranslationDirection(
    string Id,
    string DisplayName,
    string SourceLanguage,
    string TargetLanguage,
    string OutputLabel,
    string SourceLabel,
    bool EnableSlideFollowing)
{
    public bool IsBidirectional => Id.EndsWith("-bidirectional", StringComparison.Ordinal);

    public string? AsrLanguage => Id switch
    {
        "japanese-to-chinese" => "ja",
        "english-to-chinese" => "en",
        "chinese-to-japanese" or "chinese-to-english" => "zh",
        _ => null
    };

    public static TranslationDirection JapaneseToChinese { get; } = new(
        "japanese-to-chinese", "日文 → 中文", "Japanese", "Chinese", "中文同传", "日文原文", true);

    public static TranslationDirection EnglishToChinese { get; } = new(
        "english-to-chinese", "英文 → 中文", "English", "Chinese", "中文同传", "英文原文", true);

    public static TranslationDirection MixedToChinese { get; } = new(
        "mixed-to-chinese", "日英混讲 → 中文", "auto", "Chinese", "中文同传", "日英原文", true);

    public static TranslationDirection JapaneseChineseBidirectional { get; } = new(
        "japanese-chinese-bidirectional", "日文 ⇄ 中文（双向同传）", "auto", "auto", "日中双向同传", "实时原话", false);

    public static TranslationDirection EnglishChineseBidirectional { get; } = new(
        "english-chinese-bidirectional", "英文 ⇄ 中文（双向同传）", "auto", "auto", "英中双向同传", "实时原话", false);

    public static TranslationDirection ChineseToJapanese { get; } = new(
        "chinese-to-japanese", "中文 → 日语", "Chinese", "Japanese", "日语翻译", "中文原文", false);

    public static TranslationDirection ChineseToEnglish { get; } = new(
        "chinese-to-english", "中文 → 英语", "Chinese", "English", "英语翻译", "中文原文", false);

    public static IReadOnlyList<TranslationDirection> All { get; } =
        [JapaneseToChinese, EnglishToChinese, MixedToChinese,
         JapaneseChineseBidirectional, EnglishChineseBidirectional,
         ChineseToJapanese, ChineseToEnglish];

    public static TranslationDirection FromId(string? id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? MixedToChinese;

    public override string ToString() => DisplayName;
}
