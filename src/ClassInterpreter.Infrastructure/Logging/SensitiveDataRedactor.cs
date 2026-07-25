using System.Text.RegularExpressions;

namespace ClassInterpreter.Infrastructure.Logging;

public static partial class SensitiveDataRedactor
{
    public static string Redact(string value)
    {
        var bearerRedacted = BearerToken().Replace(value, "$1[REDACTED]");
        return ApiKey().Replace(bearerRedacted, "$1[REDACTED]");
    }

    [GeneratedRegex("(?i)(Authorization\\s*:\\s*Bearer\\s+)[^\\s,;]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex("(?i)((?:api[_-]?key|secret)\\s*[=:]\\s*)[^\\s,;]+")]
    private static partial Regex ApiKey();
}
