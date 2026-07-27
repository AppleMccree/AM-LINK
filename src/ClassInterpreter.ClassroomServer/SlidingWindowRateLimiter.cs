using System.Collections.Concurrent;

namespace ClassInterpreter.ClassroomServer;

/// <summary>
/// 按调用方（通常是 IP）限制窗口期内的请求次数，防止拿到课堂码的人刷匿名加入。
/// 纯内存实现，重启即清零，对单校单服务器的规模足够。
/// </summary>
public sealed class SlidingWindowRateLimiter(int maxRequests, TimeSpan window)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    public bool TryAcquire(string key, DateTimeOffset now)
    {
        var queue = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > window) queue.Dequeue();
            if (queue.Count >= maxRequests) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
