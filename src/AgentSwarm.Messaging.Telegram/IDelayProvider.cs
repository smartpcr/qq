using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Wraps <see cref="Task.Delay(TimeSpan, CancellationToken)"/> so the
/// proactive rate limiter and the 429 backoff path in
/// <see cref="TelegramMessageSender"/> can be unit-tested without sleeping
/// real wall-clock time. Tests substitute a stub that captures the
/// requested delays.
/// </summary>
public interface IDelayProvider
{
    /// <summary>
    /// Asynchronously waits for <paramref name="delay"/> or until
    /// <paramref name="ct"/> is cancelled, whichever comes first.
    /// </summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>
/// Production <see cref="IDelayProvider"/>. Simply forwards to
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// </summary>
internal sealed class TaskDelayProvider : IDelayProvider
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        // Task.Delay rejects negative spans except for Timeout.InfiniteTimeSpan.
        if (delay < TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }
        return Task.Delay(delay, ct);
    }
}
