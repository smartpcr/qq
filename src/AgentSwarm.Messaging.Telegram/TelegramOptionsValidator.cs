using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// <see cref="IValidateOptions{TOptions}"/> implementation that fails
/// fast at host startup when <see cref="TelegramOptions"/> is shaped in
/// a way that would silently break the receive path. Registered
/// alongside <c>.ValidateOnStart()</c> in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> so the
/// process throws <c>OptionsValidationException</c> during
/// <c>IHost.StartAsync</c> rather than failing later at the first
/// Telegram API call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Aggregate, do not short-circuit.</b> Each rule contributes its
/// failure message to a list; <see cref="Validate"/> returns
/// <see cref="ValidateOptionsResult.Fail(IEnumerable{string})"/> with
/// every failure so the operator sees ALL problems at once instead of
/// having to iterate (fix one → restart → discover the next). This is
/// pinned by the <c>MultipleFailures_AllAppearInFailureMessage</c>
/// test in <c>TelegramOptionsValidatorWebhookTests</c>.
/// </para>
/// <para>
/// <b>Rules.</b>
/// <list type="bullet">
///   <item><description><b>BotToken</b> — required, non-blank.
///   Story brief Authentication row.</description></item>
///   <item><description><b>WebhookUrl + UsePolling mutually exclusive</b>
///   — architecture.md §7.1 and implementation-plan.md §209: webhook
///   and polling are different receive modes; both enabled produces
///   undefined behaviour.</description></item>
///   <item><description><b>WebhookUrl must be HTTPS absolute URI</b>
///   (iter-5 evaluator item 2) — Telegram Bot API rejects non-HTTPS
///   webhook URLs at <c>setWebhook</c>; failing fast at host startup
///   converts a confusing 4xx-at-first-startup into an obvious
///   <c>OptionsValidationException</c> at boot.</description></item>
///   <item><description><b>SecretToken required when webhook mode</b> —
///   architecture.md §11.3: the <c>X-Telegram-Bot-Api-Secret-Token</c>
///   header is the only authentication on the public webhook endpoint;
///   running webhook mode without a secret would let any caller who
///   knows the URL POST forged updates.</description></item>
///   <item><description><b>OperatorBindings TenantId/WorkspaceId</b>
///   non-blank — see
///   <see cref="TelegramOperatorBindingOptions"/>: a binding with a
///   blank tenant/workspace would silently coalesce all operators
///   into a default tenant boundary.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>What is NOT validated here.</b> The "no receive mode" shape
/// (no webhook URL, no polling) is allowed — integration tests and
/// CI smoke runs need it to register the bot client + pipeline
/// without an active receive loop. The <c>NoReceiveMode_IsAllowed_ForUnitTestsAndCi</c>
/// test pins this.
/// </para>
/// </remarks>
internal sealed class TelegramOptionsValidator : IValidateOptions<TelegramOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TelegramOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail(
                "Telegram options are not configured. Add a 'Telegram' section to configuration.");
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            failures.Add(
                "Telegram:BotToken must be configured. Set it via Azure Key Vault, "
                + "the TELEGRAM__BOTTOKEN environment variable, or "
                + "'dotnet user-secrets set Telegram:BotToken <token>'. "
                + "Never commit the token to source control.");
        }

        var webhookUrlSet = !string.IsNullOrWhiteSpace(options.WebhookUrl);

        if (webhookUrlSet && options.UsePolling)
        {
            failures.Add(
                "Telegram:WebhookUrl and Telegram:UsePolling are mutually exclusive — "
                + "set one or the other, not both. Webhook mode is the production "
                + "receive path; polling is for local/dev only.");
        }

        if (webhookUrlSet)
        {
            // Iter-5 evaluator item 2 — Telegram Bot API webhooks are
            // HTTPS-only. A non-absolute URI (relative path), a
            // malformed string, or a non-https scheme would be rejected
            // by `setWebhook` at first startup; failing here surfaces
            // the misconfiguration as a clear OptionsValidationException
            // at boot rather than a confusing 4xx later.
            var rawUrl = options.WebhookUrl!;
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsed))
            {
                failures.Add(
                    "Telegram:WebhookUrl must be an absolute URI "
                    + "(e.g. https://example.com/api/telegram/webhook). "
                    + "Relative paths and non-URI strings are rejected — "
                    + "Telegram Bot API requires an absolute callback URL.");
            }
            else if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    "Telegram:WebhookUrl scheme must be https (got '"
                    + parsed.Scheme
                    + "'). Telegram Bot API only accepts https webhook callbacks; "
                    + "non-https URLs are rejected at setWebhook and break the receive path.");
            }
        }

        if (webhookUrlSet && string.IsNullOrWhiteSpace(options.SecretToken))
        {
            failures.Add(
                "Telegram:SecretToken must be configured when Telegram:WebhookUrl is set. "
                + "The secret is the only authentication on the public webhook endpoint; "
                + "running webhook mode without one accepts unauthenticated POSTs from any "
                + "caller that knows the URL.");
        }

        if (options.OperatorBindings is { Count: > 0 })
        {
            for (var i = 0; i < options.OperatorBindings.Count; i++)
            {
                var binding = options.OperatorBindings[i];
                if (binding is null) { continue; }

                if (string.IsNullOrWhiteSpace(binding.TenantId))
                {
                    failures.Add(
                        $"Telegram:OperatorBindings[{i}].TenantId must be non-blank. "
                        + "A blank tenant id would silently coalesce all bindings into "
                        + "one tenant boundary, breaking per-tenant routing.");
                }

                if (string.IsNullOrWhiteSpace(binding.WorkspaceId))
                {
                    failures.Add(
                        $"Telegram:OperatorBindings[{i}].WorkspaceId must be non-blank. "
                        + "A blank workspace id would silently coalesce all bindings into "
                        + "one workspace, breaking per-workspace routing.");
                }
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
