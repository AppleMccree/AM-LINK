using System.Text;

namespace ClassInterpreter.Infrastructure.Logging;

public static class ErrorLogWriter
{
    private static readonly object Gate = new();

    /// <summary>
    /// 把未处理异常追加到本地日志文件，返回日志路径。
    /// 内容经过脱敏，绝不写入 API Key 或 Bearer 令牌；写日志本身失败时静默放弃，
    /// 不允许日志问题再次拖垮应用。
    /// </summary>
    public static string? Append(string logDirectory, string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, $"errors-{DateTime.Now:yyyyMMdd}.log");
            var entry = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}")
                .AppendLine(SensitiveDataRedactor.Redact(exception.ToString()))
                .AppendLine()
                .ToString();
            lock (Gate)
            {
                File.AppendAllText(path, entry, Encoding.UTF8);
            }
            return path;
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
