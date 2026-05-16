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
///   <item><description><b>PollingTimeoutSeconds in [1, 50] when polling</b>
///   — Telegram <c>getUpdates</c> caps the server-side long-poll
///   timeout at 50 seconds; values &lt;= 0 degrade to short-poll and
///   burn the API quota with tight requests, values &gt; 50 are
///   rejected by Telegram. Only enforced when
///   <see cref="TelegramOptions.UsePolling"/> is <c>true</c> because
///   the field is ignored otherwise (matches the field's xmldoc and
///   the <c>ResolvePollingTimeout</c> contract in
///   <see cref="Polling.TelegramPollingService"/>).</description></item>
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

        // Polling-mode contract: Telegram's getUpdates caps the
        // server-side long-poll timeout at 50 seconds, and a value
        // <= 0 degrades to short-poll (a tight request loop that
        // burns API quota). The field is ignored when polling is
        // disabled, so only enforce the range when UsePolling is
        // true. Matches the xmldoc on
        // TelegramOptions.PollingTimeoutSeconds and the contract
        // referenced by TelegramPollingService.ResolvePollingTimeout.
        if (options.UsePolling
            && (options.PollingTimeoutSeconds < 1 || options.PollingTimeoutSeconds > 50))
        {
            failures.Add(
                "Telegram:PollingTimeoutSeconds must be in the range [1, 50] when "
                + "Telegram:UsePolling is true. Telegram's getUpdates caps the "
                + "server-side long-poll timeout at 50 seconds; values <= 0 degrade "
                + "to short-poll and burn the API quota with tight requests. "
                + $"Configured value: {options.PollingTimeoutSeconds}.");
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

        // Stage 2.7 — same TenantId/WorkspaceId guard applies to the
        // DevOperators directory consumed by StubOperatorRegistry. A
        // blank tenant would silently coalesce subscription bootstrap
        // into a single "" tenant, suppressing real per-tenant streams;
        // a blank workspace would break the alert fallback's
        // GetByWorkspaceAsync resolution (§5.6).
        if (options.DevOperators is { Count: > 0 })
        {
            for (var i = 0; i < options.DevOperators.Count; i++)
            {
                var binding = options.DevOperators[i];
                if (binding is null) { continue; }

                if (string.IsNullOrWhiteSpace(binding.TenantId))
                {
                    failures.Add(
                        $"Telegram:DevOperators[{i}].TenantId must be non-blank. "
                        + "A blank tenant id would silently coalesce all dev operators into "
                        + "one tenant boundary, breaking per-tenant swarm-event subscription.");
                }

                if (string.IsNullOrWhiteSpace(binding.WorkspaceId))
                {
                    failures.Add(
                        $"Telegram:DevOperators[{i}].WorkspaceId must be non-blank. "
                        + "A blank workspace id would break alert fallback routing via "
                        + "IOperatorRegistry.GetByWorkspaceAsync (architecture.md §5.6).");
                }
            }
        }

        // Stage 3.4 (iter-2 evaluator item 3) — UserTenantMappings is
        // the Tier 1 onboarding source of truth for
        // TelegramUserAuthorizationService. A partially invalid
        // multi-workspace mapping (one valid entry + one entry with
        // a blank TenantId / WorkspaceId / OperatorAlias) used to
        // silently skip the bad entry at /start time and authorize
        // the operator with FEWER bindings than configured. The
        // validator now rejects any mapping with a blank key, a
        // non-numeric key, or any entry missing TenantId,
        // WorkspaceId, or OperatorAlias so the operator notices
        // the misconfiguration at host startup rather than at the
        // first /start.
        if (options.UserTenantMappings is { Count: > 0 })
        {
            foreach (var pair in options.UserTenantMappings)
            {
                var key = pair.Key;
                if (string.IsNullOrWhiteSpace(key))
                {
                    failures.Add(
                        "Telegram:UserTenantMappings has an entry with a blank user-id key. "
                        + "Each key must be the Telegram user id as a numeric string "
                        + "(per architecture.md §7.1).");
                    continue;
                }

                if (!long.TryParse(
                        key.Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out _))
                {
                    failures.Add(
                        $"Telegram:UserTenantMappings[\"{key}\"] key is not a valid 64-bit "
                        + "Telegram user id (must parse as a long).");
                    continue;
                }

                var entries = pair.Value;
                if (entries is null || entries.Count == 0)
                {
                    failures.Add(
                        $"Telegram:UserTenantMappings[\"{key}\"] must contain at least one "
                        + "TelegramUserTenantMapping entry — empty arrays cannot onboard the user.");
                    continue;
                }

                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry is null)
                    {
                        failures.Add(
                            $"Telegram:UserTenantMappings[\"{key}\"][{i}] is null.");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(entry.TenantId))
                    {
                        failures.Add(
                            $"Telegram:UserTenantMappings[\"{key}\"][{i}].TenantId must be "
                            + "non-blank. A blank tenant id would let /start onboard the "
                            + "operator into the empty-string tenant and break per-tenant "
                            + "alias resolution (architecture.md lines 116-119).");
                    }

                    if (string.IsNullOrWhiteSpace(entry.WorkspaceId))
                    {
                        failures.Add(
                            $"Telegram:UserTenantMappings[\"{key}\"][{i}].WorkspaceId must be "
                            + "non-blank. A blank workspace id would silently coalesce all "
                            + "onboarded operators into a single workspace, breaking the "
                            + "multi-workspace disambiguation prompt (architecture.md §4.3).");
                    }

                    if (string.IsNullOrWhiteSpace(entry.OperatorAlias))
                    {
                        failures.Add(
                            $"Telegram:UserTenantMappings[\"{key}\"][{i}].OperatorAlias must "
                            + "be non-blank. The /handoff @alias resolver uses the alias as "
                            + "the lookup key (architecture.md §4.3); a blank alias would "
                            + "make the operator unreachable via /handoff.");
                    }
                }
            }

            // Stage 3.4 (iter-3 evaluator item 1) — the
            // operator_bindings table has a UNIQUE index on
            // (OperatorAlias, TenantId) (architecture.md lines
            // 116-119: a `/handoff @alias` lookup in a given tenant
            // must resolve to exactly one operator). Two
            // UserTenantMappings entries that share the same
            // (OperatorAlias, TenantId) — whether under the SAME
            // Telegram user (an operator with multiple workspace
            // bindings re-using one alias) or under DIFFERENT
            // Telegram users (two operators colliding on one alias)
            // — would let host startup succeed and then crash the
            // first /start that hits the unique index, leaving the
            // operator with a partial onboarding state. Detect both
            // shapes here so misconfiguration surfaces at startup.
            //
            // Iter-4 doc correction: the prior comment claimed the
            // persistence layer's HasMaxLength call applies SQLite
            // COLLATE NOCASE — that was wrong. OperatorAlias is
            // declared as TEXT(128) with no explicit collation, so
            // SQLite uses its default BINARY (case-sensitive)
            // collation and the unique index would technically
            // accept "@Alice" and "@alice" as distinct rows. The
            // validator nonetheless treats aliases case-INsensitively
            // here as an operator-intent policy: when the operator
            // configures "@Alice" in one mapping entry and "@alice"
            // in another, that's almost certainly a typo for the
            // SAME human handle, and silently registering two
            // distinct bindings would break /handoff @alice (which
            // would resolve to only one of them) and confuse the
            // operator. The validator is intentionally STRICTER
            // than the DB on this axis — defense-in-depth against
            // a common configuration mistake. Tenant ids are
            // compared case-sensitively because tenant ids are
            // opaque identifiers (architecture.md §3.1) and any
            // visible-only case difference indicates two distinct
            // tenants, not a typo.
            var aliasOwners = new Dictionary<(string Tenant, string Alias), List<string>>(
                AliasTenantKeyComparer.Instance);
            foreach (var pair in options.UserTenantMappings)
            {
                var key = pair.Key;
                if (string.IsNullOrWhiteSpace(key) || pair.Value is null)
                {
                    continue;
                }

                for (var i = 0; i < pair.Value.Count; i++)
                {
                    var entry = pair.Value[i];
                    if (entry is null
                        || string.IsNullOrWhiteSpace(entry.TenantId)
                        || string.IsNullOrWhiteSpace(entry.OperatorAlias))
                    {
                        continue;
                    }

                    var aliasKey = (entry.TenantId, entry.OperatorAlias);
                    if (!aliasOwners.TryGetValue(aliasKey, out var owners))
                    {
                        owners = new List<string>(capacity: 2);
                        aliasOwners[aliasKey] = owners;
                    }

                    owners.Add(
                        $"Telegram:UserTenantMappings[\"{key}\"][{i}]");
                }
            }

            foreach (var kvp in aliasOwners)
            {
                if (kvp.Value.Count <= 1)
                {
                    continue;
                }

                failures.Add(
                    $"Duplicate operator alias detected: \"{kvp.Key.Alias}\" in tenant "
                    + $"\"{kvp.Key.Tenant}\" is configured by {kvp.Value.Count} mapping "
                    + $"entries [{string.Join(", ", kvp.Value)}]. The operator_bindings table "
                    + "has UNIQUE (OperatorAlias, TenantId) (architecture.md lines 116-119) so "
                    + "the first /start would succeed and every subsequent /start that tries to "
                    + "register the duplicate would crash on the unique index mid-onboarding. "
                    + "Use distinct OperatorAlias values per workspace (e.g. \"@alice-alpha\", "
                    + "\"@alice-beta\") OR consolidate the duplicate entries into one row.");
            }

            // Stage 3.4 iter-4 — the operator_bindings table also has
            // a UNIQUE index on (TelegramUserId, TelegramChatId,
            // WorkspaceId). At /start time TelegramChatId is the chat
            // the operator typed /start in, so for a single user
            // every batch entry shares the same TelegramChatId. Two
            // mapping entries under the SAME user that share a
            // WorkspaceId would therefore both target the same
            // (user, chat, workspace) row and the batch upsert in
            // PersistentOperatorRegistry.RegisterManyAsync would
            // either silently coalesce them (under the old per-entry
            // RegisterAsync code path) or crash mid-batch and roll
            // back (under the iter-3 transactional path). Both
            // outcomes are wrong: the operator's intent is ambiguous
            // when one workspace is bound twice. Fail-fast at startup
            // so the misconfiguration surfaces immediately instead of
            // at the first /start.
            //
            // The check is per-user (the outer dictionary is keyed
            // by user id) — different users CAN legitimately share
            // a workspace (that is the multi-operator-per-workspace
            // pattern from architecture.md §4.3). The collision only
            // matters within a single user's mapping list.
            foreach (var pair in options.UserTenantMappings)
            {
                var key = pair.Key;
                if (string.IsNullOrWhiteSpace(key) || pair.Value is null || pair.Value.Count < 2)
                {
                    continue;
                }

                var workspaceOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                for (var i = 0; i < pair.Value.Count; i++)
                {
                    var entry = pair.Value[i];
                    if (entry is null || string.IsNullOrWhiteSpace(entry.WorkspaceId))
                    {
                        continue;
                    }

                    if (!workspaceOwners.TryGetValue(entry.WorkspaceId, out var owners))
                    {
                        owners = new List<string>(capacity: 2);
                        workspaceOwners[entry.WorkspaceId] = owners;
                    }

                    owners.Add($"Telegram:UserTenantMappings[\"{key}\"][{i}]");
                }

                foreach (var kvp in workspaceOwners)
                {
                    if (kvp.Value.Count <= 1)
                    {
                        continue;
                    }

                    failures.Add(
                        $"Duplicate workspace binding detected for Telegram user \"{key}\": "
                        + $"WorkspaceId \"{kvp.Key}\" is configured by {kvp.Value.Count} "
                        + $"mapping entries [{string.Join(", ", kvp.Value)}]. The "
                        + "operator_bindings table has UNIQUE (TelegramUserId, "
                        + "TelegramChatId, WorkspaceId) so the second /start row would "
                        + "either silently coalesce the configurations or crash the batch "
                        + "transaction. Consolidate the duplicate workspace entries into "
                        + "one row with the intended TenantId / OperatorAlias / Roles.");
                }
            }
        }

        ValidateRateLimits(options.RateLimits, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Case-sensitive tenant + case-insensitive alias comparer used by
    /// the iter-3 duplicate-alias detection loop.
    /// </summary>
    /// <remarks>
    /// Iter-4 doc correction: the prior comment claimed this comparer
    /// "matches the SQLite collation applied to OperatorAlias by the
    /// persistence layer" — that was wrong.
    /// <see cref="OperatorBindingConfiguration"/> declares
    /// <c>OperatorAlias</c> as <c>TEXT(128)</c> with no explicit
    /// collation, so SQLite uses BINARY (case-sensitive). This
    /// comparer is intentionally MORE strict than the DB on the alias
    /// axis — see the rationale block in
    /// <see cref="TelegramOptionsValidator.Validate(string?, TelegramOptions)"/>
    /// near the <c>aliasOwners</c> dictionary construction.
    /// </remarks>
    private sealed class AliasTenantKeyComparer : IEqualityComparer<(string Tenant, string Alias)>
    {
        public static readonly AliasTenantKeyComparer Instance = new();

        public bool Equals((string Tenant, string Alias) x, (string Tenant, string Alias) y) =>
            string.Equals(x.Tenant, y.Tenant, StringComparison.Ordinal)
            && string.Equals(x.Alias, y.Alias, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Tenant, string Alias) obj) => HashCode.Combine(
            obj.Tenant is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.Tenant),
            obj.Alias is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Alias));
    }

    /// <summary>
    /// Rate-limit configuration is load-bearing for the §10.4 burst
    /// envelope and for the documented Telegram per-bot / per-chat
    /// soft caps. Zero or negative values would have been silently
    /// clamped by <see cref="Sending.TokenBucketTelegramRateLimiter"/>'s
    /// <c>Math.Max(1, …)</c> guards, hiding a configuration error
    /// behind a "default-feeling" runtime behaviour. We fail fast at
    /// host startup instead so the operator notices the misconfiguration
    /// immediately. <see langword="null"/> <paramref name="options"/>
    /// is accepted — <see cref="TelegramOptions.RateLimits"/> defaults
    /// to a fresh <see cref="Sending.RateLimitOptions"/> instance via
    /// the property initialiser, but a future hand-constructed
    /// <see cref="TelegramOptions"/> could leave it null; that case is
    /// not a misconfiguration (the limiter would fall back to defaults).
    /// </summary>
    private static void ValidateRateLimits(Sending.RateLimitOptions? options, List<string> failures)
    {
        if (options is null)
        {
            return;
        }

        if (options.GlobalPerSecond <= 0)
        {
            failures.Add(
                "Telegram:RateLimits:GlobalPerSecond must be > 0 (token-bucket refill rate, tokens per second). "
                + $"Configured value: {options.GlobalPerSecond}. A non-positive value silently clamped at runtime "
                + "would mask the configuration error and degrade the §10.4 burst SLO envelope.");
        }

        if (options.GlobalBurstCapacity <= 0)
        {
            failures.Add(
                "Telegram:RateLimits:GlobalBurstCapacity must be > 0 (token-bucket capacity). "
                + $"Configured value: {options.GlobalBurstCapacity}. A non-positive value silently clamped "
                + "at runtime would mask the configuration error.");
        }

        if (options.PerChatPerMinute <= 0)
        {
            failures.Add(
                "Telegram:RateLimits:PerChatPerMinute must be > 0 (per-chat token-bucket refill rate, tokens per minute). "
                + $"Configured value: {options.PerChatPerMinute}. A non-positive value silently clamped "
                + "at runtime would mask the configuration error and risk violating Telegram's documented "
                + "20-msg/min per-chat soft cap.");
        }

        if (options.PerChatBurstCapacity <= 0)
        {
            failures.Add(
                "Telegram:RateLimits:PerChatBurstCapacity must be > 0 (per-chat token-bucket capacity). "
                + $"Configured value: {options.PerChatBurstCapacity}. A non-positive value silently clamped "
                + "at runtime would mask the configuration error and degrade the §10.4 D-BURST envelope.");
        }
    }
}
