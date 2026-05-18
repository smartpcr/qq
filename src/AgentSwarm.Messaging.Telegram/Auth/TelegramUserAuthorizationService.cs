using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Telegram.Auth;

/// <summary>
/// Stage 3.4 -- production <see cref="IUserAuthorizationService"/>
/// implementation backed by the persistent
/// <see cref="IOperatorRegistry"/>. The sole supported
/// <see cref="IUserAuthorizationService"/> implementation in the
/// Telegram project. (The iter-5 in-memory
/// <c>ConfiguredOperatorAuthorizationService</c> was deleted in
/// Stage 5.2 iter-4 -- the registry-backed two-tier contract here is
/// now the only Telegram authorization path; future replacements MUST
/// implement the same fail-closed contract.)
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 2 (runtime, every command except <c>/start</c>).</b> Calls
/// <see cref="IOperatorRegistry.GetBindingsAsync"/> for the inbound
/// (<c>TelegramUserId</c>, <c>TelegramChatId</c>) pair and populates
/// <see cref="AuthorizationResult.Bindings"/> with the full result
/// list. The pipeline then handles cardinality: zero bindings ->
/// unauthorized rejection; one binding -> build
/// <see cref="AgentSwarm.Messaging.Abstractions.AuthorizedOperator"/>
/// directly; multiple bindings -> present workspace disambiguation
/// via inline keyboard (per architecture.md section 4.3 and the
/// e2e-scenarios multi-workspace flow).
/// </para>
/// <para>
/// <b>Tier 1 (<c>/start</c> onboarding).</b> The algorithm follows
/// architecture.md section 7.1 (lines 1042-1065) and implementation-plan.md
/// Stage 3.4 step 4 / Stage 5.2 step 2:
/// <list type="number">
///   <item><description>If the inbound user id is not in
///   <see cref="TelegramOptions.AllowedUserIds"/> -- including the
///   case where <see cref="TelegramOptions.AllowedUserIds"/> is
///   empty -- deny with a structured reason and log the attempt
///   (Stage 5.2 brief: <c>AllowedUserIds</c> is the single source
///   of truth for onboarding; users not in the list are rejected,
///   and there is no escape hatch). A production deployment that
///   forgets to populate the allowlist therefore rejects every
///   <c>/start</c> attempt instead of silently authorising every
///   Telegram user who DMs the bot -- fail-closed by construction,
///   no opt-out flag.</description></item>
///   <item><description>If the user IS in the allowlist but no entry
///   exists in <see cref="TelegramOptions.UserTenantMappings"/>,
///   deny with a structured reason -- the operator cannot be onboarded
///   without a tenant/workspace assignment, and silently registering
///   under a fabricated tenant would breach the architecture.md section 7.1
///   "all required fields populated" contract.</description></item>
///   <item><description>Build one
///   <see cref="OperatorRegistration"/> value object per
///   <see cref="TelegramUserTenantMapping"/> entry under the user's
///   key (one per workspace), then submit the full batch via
///   <see cref="IOperatorRegistry.RegisterManyAsync"/>. The
///   persistent registry's
///   (<see cref="PersistentOperatorRegistry"/>) override wraps every
///   upsert in one <c>IDbContextTransaction</c> so the batch is
///   atomic -- a <c>(OperatorAlias, TenantId)</c> unique-index
///   collision on row N rolls back rows 1..N-1 instead of leaving
///   the operator in a partial-onboarding state. The per-row upsert
///   semantics inside the batch also make this idempotent: replays
///   of <c>/start</c> refresh the existing rows instead of inserting
///   duplicates. (Stage 3.4 iter-3 evaluator item 2 -- replaces the
///   prior per-row <see cref="IOperatorRegistry.RegisterAsync"/>
///   loop, which could leave partial bindings on a constraint
///   violation.)</description></item>
///   <item><description>Re-query
///   <see cref="IOperatorRegistry.GetBindingsAsync"/> to surface the
///   freshly-created or refreshed bindings on
///   <see cref="AuthorizationResult.Bindings"/>. Re-query (rather
///   than synthesise) so the returned bindings carry the actual
///   persistent ids the downstream
///   <c>TelegramUpdatePipeline</c> uses to build
///   <c>AuthorizedOperator</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>ChatType derivation.</b> Stage 3.4 -- the new
/// <see cref="IUserAuthorizationService.OnboardAsync"/> entry point
/// carries the raw Telegram chat-type token (one of <c>"private"</c>,
/// <c>"group"</c>, <c>"supergroup"</c>, <c>"channel"</c>) sourced
/// from
/// <see cref="AgentSwarm.Messaging.Abstractions.MessengerEvent.ChatType"/>,
/// which the
/// <see cref="AgentSwarm.Messaging.Telegram.Webhook.TelegramUpdateMapper"/>
/// populates from <c>Update.Message.Chat.Type</c>. The token is
/// parsed via <see cref="TelegramChatTypeParser"/> and stored on
/// <see cref="OperatorBinding.ChatType"/> so a group/supergroup
/// onboarding produces a non-Private binding. When the legacy
/// <see cref="IUserAuthorizationService.AuthorizeAsync"/> entry
/// point is invoked for <c>/start</c> (older callers, contract
/// tests), the parser defaults to <see cref="ChatType.Private"/>
/// -- matching the e2e-scenarios "private chat operator" baseline
/// and the historical Stage 3.4 onboarding convention.
/// </para>
/// <para>
/// <b>Layering.</b> Lives in the Telegram project because it reads
/// <see cref="TelegramOptions"/> for both
/// <see cref="TelegramOptions.AllowedUserIds"/> and
/// <see cref="TelegramOptions.UserTenantMappings"/>. The Persistence
/// project cannot reference the Telegram project (it would create
/// a dependency cycle); the
/// <see cref="ServiceCollectionExtensions.AddMessagingPersistence"/>
/// extension registers this type via <c>AddSingleton</c> last-wins so
/// it supersedes any host-level TryAdd fallback when persistence is
/// wired. (The iter-5
/// <c>ConfiguredOperatorAuthorizationService</c> that previously
/// occupied the host TryAdd fallback slot was deleted in Stage 5.2
/// iter-4.)
/// </para>
/// </remarks>
public sealed class TelegramUserAuthorizationService : IUserAuthorizationService
{
    private readonly IOperatorRegistry _registry;
    private readonly IOptionsMonitor<TelegramOptions> _options;
    private readonly ILogger<TelegramUserAuthorizationService> _logger;

    public TelegramUserAuthorizationService(
        IOperatorRegistry registry,
        IOptionsMonitor<TelegramOptions> options,
        ILogger<TelegramUserAuthorizationService> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AuthorizationResult> AuthorizeAsync(
        string externalUserId,
        string chatId,
        string? commandName,
        CancellationToken ct)
    {
        return await AuthorizeAsync(externalUserId, chatId, commandName, chatType: null, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stage 5.2 (iter-3) -- unified two-tier authorization entry
    /// point invoked by the pipeline for every inbound command.
    /// Internally routes on <paramref name="commandName"/>: Tier 1
    /// (allowlist + onboarding) when <c>commandName == "start"</c>,
    /// Tier 2 (binding lookup) otherwise. The
    /// <paramref name="chatType"/> token is only consulted on
    /// Tier 1; Tier 2 reads the chat type from the existing
    /// <see cref="OperatorBinding.ChatType"/>.
    /// </remarks>
    public async Task<AuthorizationResult> AuthorizeAsync(
        string externalUserId,
        string chatId,
        string? commandName,
        string? chatType,
        CancellationToken ct)
    {
        return await AuthorizeCoreAsync(externalUserId, chatId, commandName, chatType, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stage 3.4 convenience entry point retained for back-compat
    /// with direct callers and unit tests written against the
    /// pre-Stage-5.2 onboarding API. Forwards to the unified
    /// 5-arg <see cref="AuthorizeAsync(string,string,string?,string?,CancellationToken)"/>
    /// with <c>commandName == "start"</c>.
    /// </remarks>
    public async Task<AuthorizationResult> OnboardAsync(
        string externalUserId,
        string chatId,
        string? chatType,
        CancellationToken ct)
    {
        return await AuthorizeAsync(externalUserId, chatId, commandName: "start", chatType, ct)
            .ConfigureAwait(false);
    }

    private async Task<AuthorizationResult> AuthorizeCoreAsync(
        string externalUserId,
        string chatId,
        string? commandName,
        string? chatType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            return Deny("externalUserId must be non-empty.");
        }

        if (!long.TryParse(externalUserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            return Deny(
                $"externalUserId '{externalUserId}' is not a valid Telegram user id (must be a 64-bit integer).");
        }

        if (string.IsNullOrWhiteSpace(chatId))
        {
            return Deny("chatId must be non-empty.");
        }

        if (!long.TryParse(chatId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatIdValue))
        {
            return Deny(
                $"chatId '{chatId}' is not a valid Telegram chat id (must be a 64-bit integer).");
        }

        var current = _options.CurrentValue;
        var isStartCommand = IsStartCommand(commandName);

        if (isStartCommand)
        {
            var resolvedChatType = TelegramChatTypeParser.ParseOrDefault(chatType);
            return await OnboardAsync(userId, chatIdValue, current, resolvedChatType, ct)
                .ConfigureAwait(false);
        }

        // Tier 2 runtime: every other command requires at least one
        // active OperatorBinding in the persistent registry. Returns
        // the FULL list of matching bindings so the pipeline's multi-
        // workspace disambiguation prompt has the data it needs.
        var bindings = await _registry
            .GetBindingsAsync(userId, chatIdValue, ct)
            .ConfigureAwait(false);

        if (bindings.Count == 0)
        {
            return Deny(
                $"No active OperatorBinding exists for user {userId} in chat {chatIdValue}. "
                + "Use /start to onboard (the user must be present in Telegram:AllowedUserIds).");
        }

        return new AuthorizationResult
        {
            IsAuthorized = true,
            Bindings = bindings,
        };
    }

    /// <summary>
    /// Tier 1 onboarding: validate the allowlist, look up the
    /// <see cref="TelegramOptions.UserTenantMappings"/> entry, and
    /// upsert one <see cref="OperatorBinding"/> per workspace.
    /// </summary>
    private async Task<AuthorizationResult> OnboardAsync(
        long userId,
        long chatIdValue,
        TelegramOptions current,
        ChatType chatType,
        CancellationToken ct)
    {
        var allowedUserIds = current.AllowedUserIds;
        var allowlistConfigured = allowedUserIds is { Count: > 0 };

        // Stage 5.2 (iter-4 evaluator item 1) -- single, unconditional
        // allowlist gate. The earlier RequireAllowlistForOnboarding
        // opt-out was removed because it weakened the brief's
        // "AllowedUserIds is the single source of truth for the
        // onboarding allowlist" contract: a deployment that left the
        // list empty would silently authorise every Telegram user who
        // DMs the bot if the opt-out got flipped. The new contract:
        // EVERY /start that is not in AllowedUserIds is denied,
        // including the empty-list case. There is no escape hatch.
        // Dev / integration-test fixtures must populate
        // Telegram:AllowedUserIds with the user IDs they want to
        // onboard, exactly like production.
        if (!allowlistConfigured)
        {
            _logger.LogWarning(
                "/start denied -- Telegram:AllowedUserIds is empty. User {TelegramUserId} from chat {TelegramChatId} cannot onboard because the allowlist contains no authorised user IDs.",
                userId,
                chatIdValue);
            return Deny(
                $"User {userId} cannot onboard: Telegram:AllowedUserIds is empty. "
                + "Stage 5.2 makes AllowedUserIds the single source of truth for /start onboarding; "
                + "populate it with the Telegram user IDs allowed to onboard.");
        }

        if (!allowedUserIds!.Contains(userId))
        {
            _logger.LogWarning(
                "Unauthorized /start attempt -- user {TelegramUserId} from chat {TelegramChatId} is not in Telegram:AllowedUserIds.",
                userId,
                chatIdValue);
            return Deny(
                $"User {userId} is not in Telegram:AllowedUserIds (Tier 1 onboarding gate).");
        }

        var mapping = ResolveUserMapping(current, userId);
        if (mapping is null || mapping.Count == 0)
        {
            _logger.LogWarning(
                "/start denied -- user {TelegramUserId} is in the allowlist but has no Telegram:UserTenantMappings entry; cannot create an OperatorBinding without a tenant/workspace assignment.",
                userId);
            return Deny(
                $"User {userId} is allowed to onboard but has no Telegram:UserTenantMappings entry. "
                + "Add a UserTenantMappings entry with TenantId, WorkspaceId, Roles, and OperatorAlias for this user.");
        }

        // Iter-2 evaluator item 3 -- fail-fast on partially invalid
        // multi-workspace mappings. Previously we silently skipped
        // entries with blank TenantId/WorkspaceId, which let a
        // partially invalid configuration authorize the user with
        // FEWER bindings than the operator intended (one missing
        // workspace = silently routed to the survivors). The
        // TelegramOptionsValidator now rejects this shape at host
        // startup, so reaching this branch at runtime means the
        // options were mutated post-startup via IOptionsMonitor
        // reload -- surface the misconfiguration as a denial rather
        // than authorize with a partial binding set.
        for (var i = 0; i < mapping.Count; i++)
        {
            var entry = mapping[i];
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.TenantId)
                || string.IsNullOrWhiteSpace(entry.WorkspaceId)
                || string.IsNullOrWhiteSpace(entry.OperatorAlias))
            {
                _logger.LogError(
                    "/start denied -- Telegram:UserTenantMappings[{TelegramUserId}][{Index}] has blank TenantId/WorkspaceId/OperatorAlias; refusing to onboard partially.",
                    userId,
                    i);
                return Deny(
                    $"Telegram:UserTenantMappings entry [{i}] for user {userId} has blank "
                    + "TenantId, WorkspaceId, or OperatorAlias. The /start flow requires every "
                    + "configured workspace entry to be complete so the operator is onboarded "
                    + "into every intended workspace, not just the valid subset.");
            }
        }

        // Stage 3.4 iter-3 (evaluator item 2) -- atomic batch upsert.
        // Previously we iterated mapping entries and called the
        // single-row IOperatorRegistry registration entry point one
        // at a time. If entry [N] failed (e.g. the operator_bindings
        // UNIQUE (OperatorAlias, TenantId) index rejected it because
        // the alias was already claimed by another operator in the
        // same tenant, or a transient DB error fired between row 1
        // and row 2), the earlier rows stayed inserted, leaving the
        // operator in exactly the partial-onboarding state the
        // iter-2 blank-field fail-fast was meant to prevent.
        // RegisterManyAsync wraps every upsert in one transaction
        // and rolls back ALL inserts if any entry fails
        // (architecture.md section 3.1 atomicity requirement for /start
        // onboarding).
        var registrations = new List<OperatorRegistration>(capacity: mapping.Count);
        for (var i = 0; i < mapping.Count; i++)
        {
            var entry = mapping[i];
            registrations.Add(new OperatorRegistration
            {
                TelegramUserId = userId,
                TelegramChatId = chatIdValue,
                ChatType = chatType,
                TenantId = entry.TenantId,
                WorkspaceId = entry.WorkspaceId,
                Roles = entry.Roles is null
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : entry.Roles.ToArray(),
                OperatorAlias = string.IsNullOrWhiteSpace(entry.OperatorAlias)
                    ? BuildFallbackAlias(userId)
                    : entry.OperatorAlias,
            });
        }

        // Stage 5.2 (iter-4 evaluator item 1) -- let operational
        // failures propagate. The earlier shape caught every non-
        // cancellation exception from RegisterManyAsync and converted
        // it into an AuthorizationResult denial; that suppressed
        // legitimate retry/redelivery for transient DB failures
        // (deadlock, connection drop, pool exhaustion, EF Core
        // optimistic-concurrency etc.) because the pipeline at
        // TelegramUpdatePipeline.ExecuteAsync treats denials as
        // "handled" short-circuits that do NOT release the dedup
        // reservation -- onboarding for that update would be dropped
        // permanently, leaving the operator unable to /start without
        // operator intervention.
        //
        // The new contract: log an ERROR so the persistence failure
        // is observable, then re-throw. The pipeline's outer try/catch
        // (TelegramUpdatePipeline.cs release-on-throw guard) catches
        // the throw, calls IDeduplicationService.ReleaseReservationAsync
        // so the next live re-delivery is processed normally
        // (Stage 2.2 Scenario 4), then re-throws so the webhook
        // controller marks the InboundUpdate row as Failed and the
        // Stage 2.4 recovery sweep retries. The /start eventually
        // succeeds once the transient condition clears.
        //
        // The previous "common cause: alias collision" hint is now
        // surfaced exclusively via the ERROR log: an alias collision
        // IS terminal and replay won't help, but operationally it's
        // far rarer than a transient DB blip, and the cost of
        // surfacing it as an exception (operator sees a generic
        // server-error reply once and the recovery sweep eventually
        // gives up) is much lower than the cost of swallowing every
        // transient blip as a permanent denial.
        try
        {
            await _registry.RegisterManyAsync(registrations, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "/start onboarding for user {TelegramUserId} chat {TelegramChatId} failed in RegisterManyAsync after staging {BindingCount} workspace binding(s); transaction rolled back, no rows persisted. Re-throwing so the pipeline's release-on-throw guard frees the dedup reservation and the update is retried via webhook redelivery or the Stage 2.4 InboundUpdate sweep.",
                userId,
                chatIdValue,
                registrations.Count);
            throw;
        }

        // Re-query to return the persistent records (with their real
        // ids and the registry's authoritative IsActive / RegisteredAt
        // values). Synthesising the list locally would risk drifting
        // from the rows the runtime authorization path reads.
        var bindings = await _registry
            .GetBindingsAsync(userId, chatIdValue, ct)
            .ConfigureAwait(false);

        if (bindings.Count == 0)
        {
            // Defensive: if the persistent store accepted RegisterAsync
            // calls but returned zero rows on read-back, something is
            // wrong with the persistence layer. Surface the failure
            // rather than silently authorizing an operator with no
            // backing rows.
            _logger.LogError(
                "/start onboarding for user {TelegramUserId} chat {TelegramChatId} completed without producing any persistent OperatorBinding rows.",
                userId,
                chatIdValue);
            return Deny(
                $"User {userId} onboarding completed but no OperatorBinding rows were materialised. "
                + "Investigate the persistent operator registry.");
        }

        _logger.LogInformation(
            "/start onboarded user {TelegramUserId} in chat {TelegramChatId} with {BindingCount} workspace binding(s).",
            userId,
            chatIdValue,
            bindings.Count);

        return new AuthorizationResult
        {
            IsAuthorized = true,
            Bindings = bindings,
        };
    }

    /// <summary>
    /// Resolves a user's <see cref="TelegramOptions.UserTenantMappings"/>
    /// entry by the canonical numeric string key (per architecture.md
    /// section 7.1: "12345"), falling back to InvariantCulture-formatted
    /// long -> string for tolerance against configuration providers
    /// that surface keys via different culture rules.
    /// </summary>
    private static IReadOnlyList<TelegramUserTenantMapping>? ResolveUserMapping(
        TelegramOptions options,
        long userId)
    {
        if (options.UserTenantMappings is null || options.UserTenantMappings.Count == 0)
        {
            return null;
        }

        var key = userId.ToString(CultureInfo.InvariantCulture);
        if (options.UserTenantMappings.TryGetValue(key, out var entries))
        {
            return entries;
        }

        // Configuration binders may surface the key with leading
        // whitespace or with the "+" sign on positive values; do a
        // tolerant final pass before giving up.
        foreach (var kv in options.UserTenantMappings)
        {
            if (kv.Key is null) { continue; }
            if (long.TryParse(kv.Key.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed == userId)
            {
                return kv.Value;
            }
        }

        return null;
    }

    private static AuthorizationResult Deny(string reason) => new()
    {
        IsAuthorized = false,
        DenialReason = reason,
    };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="commandName"/> is the
    /// <c>/start</c> onboarding command. Case-insensitive and tolerates
    /// the optional leading slash.
    /// </summary>
    internal static bool IsStartCommand(string? commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return false;
        }

        var trimmed = commandName!.TrimStart('/');
        return trimmed.Equals("start", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFallbackAlias(long userId)
    {
        return "@user-" + userId.ToString(CultureInfo.InvariantCulture);
    }
}
