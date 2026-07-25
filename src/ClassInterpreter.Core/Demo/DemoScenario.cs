using ClassInterpreter.Core.Slides;
using ClassInterpreter.Core.Speech;

namespace ClassInterpreter.Core.Demo;

public sealed record DemoUtterance(
    TimeSpan At,
    string Source,
    string Chinese,
    string Language,
    int TargetPage,
    string Japanese,
    string English)
{
    public string SourceFor(TranslationDirection direction) =>
        direction == TranslationDirection.MixedToChinese ? Source : Chinese;

    public string TargetFor(TranslationDirection direction) => direction.Id switch
    {
        "chinese-to-japanese" => Japanese,
        "chinese-to-english" => English,
        _ => Chinese
    };
}

public sealed record DemoScenario(
    SlideDocument Slides,
    IReadOnlyList<DemoUtterance> Utterances,
    string AnalysisMarkdown)
{
    public static DemoScenario Create()
    {
        var slides = new SlideDocument("demo",
        [
            new SlidePage(1, "研究背景", "Transformer 与序列建模的研究背景", string.Empty),
            new SlidePage(2, "Self-Attention", "Query Key Value、scaled dot-product attention 与 softmax", string.Empty),
            new SlidePage(3, "实验结果", "Validation accuracy、ablation study 与 baseline comparison", string.Empty),
            new SlidePage(4, "下一步工作", "行动项、负责人、deadline 与下次组会", string.Empty)
        ]);
        var utterances = new DemoUtterance[]
        {
            new(TimeSpan.FromSeconds(1), "Today we will discuss Transformer の研究背景。", "今天我们讨论 Transformer 的研究背景。", "mixed", 1, "今日はTransformerの研究背景について議論します。", "Today we will discuss the research background of Transformers."),
            new(TimeSpan.FromSeconds(4), "Self-attention では query、key、value を使います。", "在自注意力机制中，我们使用 query、key 和 value。", "mixed", 2, "自己注意機構では、query、key、valueを使用します。", "In self-attention, we use query, key, and value."),
            new(TimeSpan.FromSeconds(7), "The validation accuracy improved by three percent compared with the baseline.", "与基线相比，验证准确率提高了三个百分点。", "en", 3, "ベースラインと比較して、検証精度が3パーセントポイント向上しました。", "The validation accuracy improved by three percentage points compared with the baseline."),
            new(TimeSpan.FromSeconds(10), "次回までに ablation study を追加してください。担当は李さんです。", "请在下次会议前补充消融实验，负责人是李同学。", "ja", 4, "次回のミーティングまでにアブレーション実験を追加してください。担当は李さんです。", "Please add the ablation study before the next meeting. Li is responsible.")
        };
        const string analysis = """
            ## 课堂摘要

            本次演示介绍 Transformer 的研究背景、自注意力机制、实验结果及下一步工作。

            ## 核心知识点

            - Self-Attention 使用 Query、Key、Value 计算相关性。
            - 当前实验相较 baseline 的验证准确率提高 3 个百分点。

            ## 行动项与截止日期

            - 李同学：下次组会前补充 ablation study。

            ## 待确认疑问

            - “下次组会”的具体日期尚未指定。

            ## 复习提纲

            1. Transformer 背景
            2. Self-Attention 计算流程
            3. Baseline 与实验结果
            4. Ablation study 计划
            """;
        return new DemoScenario(slides, utterances, analysis);
    }
}
