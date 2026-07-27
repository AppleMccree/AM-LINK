using System.Net;

namespace ClassInterpreter.Infrastructure.Classrooms;

/// <summary>课堂服务器明确拒绝请求（4xx 业务错误）时抛出，携带状态码供重试策略判断。</summary>
public sealed class ClassroomServerException(string message, HttpStatusCode statusCode) : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public enum OutboxRetryDecision
{
    Retry,
    Drop
}

public static class ClassroomOutboxPolicy
{
    /// <summary>
    /// 决定离线队列里一条消息失败后的去留。
    /// 服务器业务拒绝（课堂已结束、数据无效等 4xx）重试永远不会成功，立即丢弃；
    /// 429、5xx、超时和断网都属于暂时性问题，必须一直保留到恢复后补传。
    /// </summary>
    public static OutboxRetryDecision Decide(Exception failure, int attemptsSoFar)
    {
        if (failure is ClassroomServerException server
            && (int)server.StatusCode is >= 400 and < 500
            && server.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout))
        {
            return OutboxRetryDecision.Drop;
        }

        return OutboxRetryDecision.Retry;
    }

    public static TimeSpan RetryDelay(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(attempts, 0, 5))));
}
