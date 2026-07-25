namespace ClassInterpreter.Core.Learning;

public static class AiUsagePricing
{
    // Alibaba Cloud Model Studio Singapore list prices, checked 2026-07-20.
    // Free quotas, promotions, caching discounts, taxes and rounding are intentionally not applied.
    public static decimal EstimateUsd(AiUsageRecord record)
    {
        var model = record.Model.Trim().ToLowerInvariant();
        if (model.StartsWith("qwen3-asr-flash-realtime", StringComparison.Ordinal)
            || model.StartsWith("fun-asr-realtime", StringComparison.Ordinal))
        {
            return (decimal)record.AudioMilliseconds / 1000m * 0.000090m;
        }

        var (inputPerMillion, outputPerMillion) = model switch
        {
            "qwen-mt-flash" => (0.16m, 0.49m),
            "qwen-flash" => (0.05m, 0.40m),
            "qwen3.7-plus" => (0.40m, 1.60m),
            _ => (0m, 0m)
        };
        return record.EstimatedInputTokens / 1_000_000m * inputPerMillion
             + record.EstimatedOutputTokens / 1_000_000m * outputPerMillion;
    }

    public static decimal EstimateUsd(IEnumerable<AiUsageRecord> records) => records.Sum(EstimateUsd);

    public static bool IsSupported(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        return normalized.StartsWith("qwen3-asr-flash-realtime", StringComparison.Ordinal)
               || normalized.StartsWith("fun-asr-realtime", StringComparison.Ordinal)
               || normalized is "qwen-mt-flash" or "qwen-flash" or "qwen3.7-plus";
    }

    public static string FormatUsd(decimal amount) => amount switch
    {
        >= 1m => $"US${amount:0.00}",
        >= 0.01m => $"US${amount:0.0000}",
        _ => $"US${amount:0.000000}"
    };
}
