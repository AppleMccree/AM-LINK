namespace ClassInterpreter.Infrastructure.Qwen;

public sealed class QwenProviderException(string code)
    : Exception($"千问语音服务返回错误：{code}")
{
    public string Code { get; } = code;
}
