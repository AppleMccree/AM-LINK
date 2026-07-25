using System.Text.RegularExpressions;

namespace ClassInterpreter.Infrastructure.Qwen;

public static partial class QwenEndpoint
{
    public static Uri Singapore(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || !SafeWorkspaceId().IsMatch(workspaceId))
        {
            throw new ArgumentException("Workspace ID 格式无效。", nameof(workspaceId));
        }

        return new Uri($"wss://{workspaceId}.ap-southeast-1.maas.aliyuncs.com/api-ws/v1/realtime?model={QwenAsrProtocol.Model}");
    }

    public static Uri SingaporeTranslation(string workspaceId)
    {
        _ = Singapore(workspaceId);
        return new Uri($"https://{workspaceId}.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions");
    }

    public static Uri SingaporeTranslationFallback() =>
        new("https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{1,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeWorkspaceId();
}
