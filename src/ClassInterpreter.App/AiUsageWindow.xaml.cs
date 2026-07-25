using System.Text;
using System.Windows;
using ClassInterpreter.Core.Learning;

namespace ClassInterpreter.App;

public partial class AiUsageWindow : Window
{
    private readonly Func<Task<IReadOnlyList<AiUsageRecord>>> _load;

    public AiUsageWindow(Func<Task<IReadOnlyList<AiUsageRecord>>> load)
    {
        InitializeComponent();
        _load = load;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var rows = await _load();
            var today = DateOnly.FromDateTime(DateTime.Now);
            TodayText.Text = Compact(rows.Where(row => row.Day == today));
            MonthText.Text = Compact(rows.Where(row => row.Day.Year == today.Year && row.Day.Month == today.Month));
            AllText.Text = Compact(rows);
            DetailsBox.Text = RenderDetails(rows);
        }
        catch (Exception exception)
        {
            DetailsBox.Text = $"暂时无法读取用量：{exception.Message}";
        }
    }

    private static string Compact(IEnumerable<AiUsageRecord> source)
    {
        var rows = source.ToArray();
        var minutes = rows.Sum(row => row.AudioMilliseconds) / 60000d;
        var requests = rows.Sum(row => row.RequestCount);
        var tokens = rows.Sum(row => row.EstimatedInputTokens + row.EstimatedOutputTokens);
        var cost = AiUsagePricing.EstimateUsd(rows);
        var unpriced = rows.Any(row => !AiUsagePricing.IsSupported(row.Model)) ? " + 未计价模型" : string.Empty;
        return $"约 {AiUsagePricing.FormatUsd(cost)}{unpriced}\n{minutes:0.0} 分钟 · {requests:N0} 次\n约 {tokens:N0} Token";
    }

    private static string RenderDetails(IReadOnlyList<AiUsageRecord> rows)
    {
        if (rows.Count == 0) return "还没有新版本产生的AI用量记录。";
        var builder = new StringBuilder();
        foreach (var group in rows.GroupBy(row => row.Kind).OrderBy(group => group.Key))
        {
            var title = group.Key switch
            {
                AiUsageKind.SpeechRecognition => "实时语音识别",
                AiUsageKind.Translation => "课堂翻译",
                AiUsageKind.AiTutor => "问 AI",
                AiUsageKind.StudyPack => "学习包总结",
                _ => group.Key.ToString()
            };
            builder.AppendLine($"【{title}】");
            builder.AppendLine($"请求：{group.Sum(row => row.RequestCount):N0}　失败：{group.Sum(row => row.FailureCount):N0}");
            if (group.Key == AiUsageKind.SpeechRecognition)
                builder.AppendLine($"音频：{group.Sum(row => row.AudioMilliseconds) / 60000d:0.0} 分钟");
            else
                builder.AppendLine($"输入：{group.Sum(row => row.InputCharacters):N0} 字符　输出：{group.Sum(row => row.OutputCharacters):N0} 字符");
            builder.AppendLine($"估算Token：{group.Sum(row => row.EstimatedInputTokens + row.EstimatedOutputTokens):N0}");
            builder.AppendLine(group.Any(row => !AiUsagePricing.IsSupported(row.Model))
                ? "预估费用：暂无价格"
                : $"预估费用：{AiUsagePricing.FormatUsd(AiUsagePricing.EstimateUsd(group))}");
            builder.AppendLine($"模型：{string.Join("、", group.Select(row => row.Model).Distinct())}").AppendLine();
        }
        builder.AppendLine("计价口径：新加坡站公开美元表价（2026-07-20）；免费额度、促销、缓存优惠、税费及平台取整未计入。实际扣费请以千问控制台账单为准。");
        return builder.ToString();
    }
}
