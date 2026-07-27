using System.Security.Cryptography;
using System.Text;

namespace ClassInterpreter.Core.Sessions;

public static class TranscriptIdentity
{
    /// <summary>
    /// 由会话 Id 和服务商片段 Id 共同派生字幕主键。
    /// 服务商只保证片段 Id 在单个识别会话内唯一；如果不混入会话 Id，
    /// 两节课出现相同片段 Id 时新字幕会覆盖旧课堂的数据库记录。
    /// </summary>
    public static Guid CreateSegmentId(Guid sessionId, string providerSegmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSegmentId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId:N}:{providerSegmentId}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
