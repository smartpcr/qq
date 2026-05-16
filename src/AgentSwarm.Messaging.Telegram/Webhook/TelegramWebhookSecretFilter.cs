using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Telegram.Webhook;

/// <summary>
/// <see cref="IEndpointFilter"/> that validates the
/// <c>X-Telegram-Bot-Api-Secret-Token</c> header against the configured
/// <see cref="TelegramOptions.SecretToken"/>. Rejects mismatches with
/// HTTP 403 BEFORE the controller deserializes the body or persists
/// any durable state, so an attacker who can hit the public webhook
/// URL but does not know the secret cannot create
/// <see cref="Abstractions.InboundUpdate"/> rows or trigger downstream
/// work (architecture.md §11 security; implementation-plan.md §181).
/// </summary>
/// <remarks>
/// <para>
/// <b>Constant-time comparison.</b> Header validation uses
/// <see cref="CryptographicOperations.FixedTimeEquals"/> so that an
/// attacker cannot mount a per-byte timing attack against the secret.
/// </para>
/// <para>
/// <b>Missing or empty configuration.</b> If
/// <see cref="TelegramOptions.SecretToken"/> is <c>null</c> or
/// whitespace the filter rejects ALL requests — production webhook
/// mode requires a secret per the
/// <see cref="TelegramOptionsValidator"/> startup check, so a request
/// arriving here without a configured secret indicates either a
/// misconfiguration or a deliberate fail-safe bypass attempt; either
/// way 403 is the right answer.
/// </para>
/// </remarks>
public sealed class TelegramWebhookSecretFilter : IEndpointFilter
{
    /// <summary>Header name Telegram echoes when posting webhook
    /// updates with a <c>secret_token</c> configured via
    /// <c>setWebhook</c>.</summary>
    public const string HeaderName = "X-Telegram-Bot-Api-Secret-Token";

    private readonly IOptionsMonitor<TelegramOptions> _options;
    private readonly ILogger<TelegramWebhookSecretFilter> _logger;

    public TelegramWebhookSecretFilter(
        IOptionsMonitor<TelegramOptions> options,
        ILogger<TelegramWebhookSecretFilter> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var expected = _options.CurrentValue.SecretToken;
        if (string.IsNullOrWhiteSpace(expected))
        {
            _logger.LogWarning(
                "TelegramWebhookSecretFilter rejected request: SecretToken is not configured. RemoteIp={RemoteIp}",
                context.HttpContext.Connection.RemoteIpAddress);
            return new ValueTask<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        var supplied = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!ConstantTimeEquals(expected, supplied))
        {
            _logger.LogWarning(
                "TelegramWebhookSecretFilter rejected request: header mismatch. RemoteIp={RemoteIp} HasHeader={HasHeader}",
                context.HttpContext.Connection.RemoteIpAddress,
                !string.IsNullOrEmpty(supplied));
            return new ValueTask<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return next(context);
    }

    private static bool ConstantTimeEquals(string expected, string supplied)
    {
        // FixedTimeEquals only when lengths match; otherwise we return
        // false WITHOUT a length-revealing branch on the supplied value
        // by clamping the comparison size. The simplest such pattern is
        // to make both spans the same length: pad/truncate the supplied
        // value into a buffer of the expected length and remember the
        // length mismatch separately.
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(supplied);
        if (expectedBytes.Length != suppliedBytes.Length)
        {
            // Still perform a fixed-time compare against a same-length
            // dummy so the early-return does not leak the length of the
            // expected secret to a precise enough timer.
            var dummy = new byte[expectedBytes.Length];
            _ = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, dummy);
            return false;
        }
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
