namespace ClassInterpreter.Core.Configuration;

using ClassInterpreter.Core.Speech;

public sealed record AppSettings
{
    public string DataRoot { get; init; } = @"D:\Codex\ClassInterpreter";

    public string QwenEndpoint { get; init; } = "wss://dashscope-intl.aliyuncs.com";

    public string WorkspaceId { get; init; } = string.Empty;

    public string ClassroomServerUrl { get; init; } = "https://classroom.am-link.app";

    public int AudioRetentionDays { get; init; } = 14;

    public string TranslationDirectionId { get; init; } = TranslationDirection.MixedToChinese.Id;
}
