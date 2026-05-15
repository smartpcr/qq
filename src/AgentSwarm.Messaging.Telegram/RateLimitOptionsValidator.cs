using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// <see cref="IValidateOptions{TOptions}"/> implementation that rejects
/// invalid <see cref="RateLimitOptions"/> values (e.g.,
/// <c>GlobalPerSecond = 0</c> or <c>PerChatPerMinute = -1</c>) at host
/// startup. Registered alongside <c>.ValidateOnStart()</c> in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> so the
/// process throws <see cref="OptionsValidationException"/> during
/// <c>IHost.StartAsync</c> rather than producing an
/// <see cref="System.ArgumentOutOfRangeException"/> later when the
/// <see cref="TokenBucketRateLimiter"/> singleton is first resolved.
/// </summary>
internal sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail(
                "Telegram:RateLimits options are not configured. Add a 'Telegram:RateLimits' section to configuration.");
        }

        var failures = new List<string>();

        if (options.GlobalPerSecond <= 0)
        {
            failures.Add(
                $"Telegram:RateLimits:GlobalPerSecond must be > 0 (configured value: {options.GlobalPerSecond}).");
        }
        if (options.GlobalBurstCapacity < 1)
        {
            failures.Add(
                $"Telegram:RateLimits:GlobalBurstCapacity must be >= 1 (configured value: {options.GlobalBurstCapacity}).");
        }
        if (options.PerChatPerMinute <= 0)
        {
            failures.Add(
                $"Telegram:RateLimits:PerChatPerMinute must be > 0 (configured value: {options.PerChatPerMinute}).");
        }
        if (options.PerChatBurstCapacity < 1)
        {
            failures.Add(
                $"Telegram:RateLimits:PerChatBurstCapacity must be >= 1 (configured value: {options.PerChatBurstCapacity}).");
        }
        if (options.PerChatIdleEvictionMinutes <= 0)
        {
            failures.Add(
                $"Telegram:RateLimits:PerChatIdleEvictionMinutes must be > 0 (configured value: {options.PerChatIdleEvictionMinutes}).");
        }
        if (options.PerChatEvictionThreshold < 1)
        {
            failures.Add(
                $"Telegram:RateLimits:PerChatEvictionThreshold must be >= 1 (configured value: {options.PerChatEvictionThreshold}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
