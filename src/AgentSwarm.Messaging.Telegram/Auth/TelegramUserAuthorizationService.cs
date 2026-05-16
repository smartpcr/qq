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
/// Stage 3.4 — production <see cref="IUserAuthorizationService"/>
/// implementation backed by the persistent
/// <see cref="IOperatorRegistry"/>. Replaces the iter-5 in-memory
/// <see cref="ConfiguredOperatorAuthorizationService"/> when the
/// persistence layer is wired.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 2 (runtime, every command except <c>/start</c>).</b> Calls
/// <see cref="IOperatorRegistry.GetBindingsAsync"/> for the inbound
/// (<c>TelegramUserId</c>, <c>TelegramChatId</c>) pair and populates
/// <see cref="AuthorizationResult.Bindings"/> with the full result
/// list. The pipeline then handles cardinality: zero bindings →
/// unauthorized rejection; one binding → build
/// <see cref="AgentSwarm.Messaging.Abstractions.AuthorizedOperator"/>
/// directly; multiple bindings → present workspace disambiguation
/// via inline keyboard (per architecture.md §4.3 and the
/// e2e-scenarios multi-workspace flow).
/// </para>
/// <para>
/// <b>Tier 1 (<c>/start</c> onboarding).</b> The algorithm follows
/// architecture.md §7.1 (lines 1042–1065) and implementation-plan.md
/// Stage 3.4 step 4:
/// <list type="number">
///   <item><description>If the inbound user id is not in
///   <see cref="TelegramOptions.AllowedUserIds"/>, deny with a
///   structured reason and log the attempt (the Stage 3.4 brief
///   "respond with an 'unauthorized' message and log the attempt").
///   When <see cref="TelegramOptions.AllowedUserIds"/> is empty, the
///   gate is FAIL-CLOSED by default
///   (<see cref="TelegramOptions.RequireAllowlistForOnboarding"/>
///   defaults to <c>true</c>) so a production deployment that
///   forgets to populate the allowlist rejects every <c>/start</c>
///   attempt instead of silently authorising every Telegram user
///   who DMs the bot. Dev / integration-test fixtures that need the
///   prior open-by-default semantics must explicitly set
///   <c>Telegram:RequireAllowlistForOnboarding=false</c>.</description></item>
///   <item><description>If the user IS in the allowlist but no entry
///   exists in <see cref="TelegramOptions.UserTenantMappings"/>,
///   deny with a structured reason — the operator cannot be onboarded
///   without a tenant/workspace assignment, and silently registering
///   under a fabricated tenant would breach the architecture.md §7.1
///   "all required fields populated" contract.</description></item>
///   <item><description>Build one
///   <see cref="OperatorRegistration"/> value object per
///   <see cref="TelegramUserTenantMapping"/> entry under the user's
///   key (one per workspace), then submit the full batch via
///   <see cref="IOperatorRegistry.RegisterManyAsync"/>. The
///   persistent registry's
///   (<see cref="PersistentOperatorRegistry"/>) override wraps every
///   upsert in one <c>IDbContextTransaction</c> so the batch is
///   atomic — a <c>(OperatorAlias, TenantId)</c> unique-index
///   collision on row N rolls back rows 1..N-1 instead of leaving
///   the operator in a partial-onboarding state. The per-row upsert
///   semantics inside the batch also make this idempotent: replays
///   of <c>/start</c> refresh the existing rows instead of inserting
///   duplicates. (Stage 3.4 iter-3 evaluator item 2 — replaces the
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
/// <b>ChatType derivation.</b> Stage 3.4 — the new
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
/// — matching the e2e-scenarios "private chat operator" baseline
/// and the historical
/// <see cref="ConfiguredOperatorAuthorizationService"/> convention.
/// </para>
/// <para>
/// <b>Layering.</b> Lives in the Telegram project (alongside
/// <see cref="ConfiguredOperatorAuthorizationService"/>) because it
/// reads <see cref="TelegramOptions"/> for both
/// <see cref="TelegramOptions.AllowedUserIds"/> and
/// <see cref="TelegramOptions.UserTenantMappings"/>. The Persistence
/// project cannot reference the Telegram project (it would create
/// a dependency cycle); the
/// <see cref="ServiceCollectionExtensions.AddMessagingPersistence"/>
/// extension registers this type via <c>AddSingleton</c> last-wins so
/// it supersedes the worker host's
/// <see cref="ConfiguredOperatorAuthorizationService"/> TryAdd
/// fallback when persistence is wired.
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
        return await AuthorizeCoreAsync(externalUserId, chatId, commandName, chatType: null, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stage 3.4 — preferred entry point for the <c>/start</c>
    /// onboarding path: carries the inbound Telegram chat-type token
    /// so the freshly-created
    /// <see cref="OperatorBinding.ChatType"/> reflects the real chat
    /// kind (Private / Group / Supergroup) instead of the
    /// <see cref="ChatType.Private"/> default the pre-Stage-3.4
    /// signature was forced to assume.
    /// </remarks>
    public async Task<AuthorizationResult> OnboardAsync(
        string externalUserId,
        string chatId,
        string? chatType,
        CancellationToken ct)
    {
        return await AuthorizeCoreAsync(externalUserId, chatId, commandName: "start", chatType, ct)
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

        // Iter-5 evaluator item 2 — fail-closed when the allowlist is
        // unset. The default value of RequireAllowlistForOnboarding is
        // `true` (production), so a deployment that forgets to populate
        // Telegram:AllowedUserIds rejects every /start instead of
        // silently authorising every Telegram user who DMs the bot.
        // Dev / integration-test fixtures that rely on the prior
        // open-by-default behaviour must explicitly opt in by setting
        // Telegram:RequireAllowlistForOnboarding=false.
        if (!allowlistConfigured && current.RequireAllowlistForOnboarding)
        {
            _logger.LogError(
                "/start denied — Telegram:AllowedUserIds is empty AND Telegram:RequireAllowlistForOnboarding is true (the fail-closed default). User {TelegramUserId} from chat {TelegramChatId} cannot onboard. Populate AllowedUserIds for production or set RequireAllowlistForOnboarding=false for dev/test fixtures.",
                userId,
                chatIdValue);
            return Deny(
                "Telegram:AllowedUserIds is empty AND Telegram:RequireAllowlistForOnboarding is true. "
                + "The Stage 3.4 onboarding gate is fail-closed by default: populate AllowedUserIds with "
                + "the operators allowed to onboard, OR set RequireAllowlistForOnboarding=false in "
                + "dev/integration-test fixtures.");
        }

        if (allowlistConfigured && !allowedUserIds!.Contains(userId))
        {
            _logger.LogWarning(
                "Unauthorized /start attempt — user {TelegramUserId} from chat {TelegramChatId} is not in Telegram:AllowedUserIds.",
                userId,
                chatIdValue);
            return Deny(
                $"User {userId} is not in Telegram:AllowedUserIds (Tier 1 onboarding gate).");
        }

        var mapping = ResolveUserMapping(current, userId);
        if (mapping is null || mapping.Count == 0)
        {
            _logger.LogWarning(
                "/start denied — user {TelegramUserId} is in the allowlist but has no Telegram:UserTenantMappings entry; cannot create an OperatorBinding without a tenant/workspace assignment.",
                userId);
            return Deny(
                $"User {userId} is allowed to onboard but has no Telegram:UserTenantMappings entry. "
                + "Add a UserTenantMappings entry with TenantId, WorkspaceId, Roles, and OperatorAlias for this user.");
        }

        // Iter-2 evaluator item 3 — fail-fast on partially invalid
        // multi-workspace mappings. Previously we silently skipped
        // entries with blank TenantId/WorkspaceId, which let a
        // partially invalid configuration authorize the user with
        // FEWER bindings than the operator intended (one missing
        // workspace = silently routed to the survivors). The
        // TelegramOptionsValidator now rejects this shape at host
        // startup, so reaching this branch at runtime means the
        // options were mutated post-startup via IOptionsMonitor
        // reload — surface the misconfiguration as a denial rather
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
                    "/start denied — Telegram:UserTenantMappings[{TelegramUserId}][{Index}] has blank TenantId/WorkspaceId/OperatorAlias; refusing to onboard partially.",
                    userId,
                    i);
                return Deny(
                    $"Telegram:UserTenantMappings entry [{i}] for user {userId} has blank "
                    + "TenantId, WorkspaceId, or OperatorAlias. The /start flow requires every "
                    + "configured workspace entry to be complete so the operator is onboarded "
                    + "into every intended workspace, not just the valid subset.");
            }
        }

        // Stage 3.4 iter-3 (evaluator item 2) — atomic batch upsert.
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
        // (architecture.md §3.1 atomicity requirement for /start
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

        try
        {
            await _registry.RegisterManyAsync(registrations, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The transaction has already rolled back via the
            // PersistentOperatorRegistry's using-disposal contract
            // (no rows persisted from this /start). Log a structured
            // ERROR so the operator can correlate the registry
            // failure with the denial they see in Telegram, then
            // deny the entire /start — half-onboarded state is
            // worse than no onboarding because subsequent commands
            // would route to a subset of workspaces with no
            // operator-visible signal.
            _logger.LogError(
                ex,
                "/start denied — RegisterManyAsync failed for user {TelegramUserId} chat {TelegramChatId} after staging {BindingCount} workspace binding(s); transaction rolled back, no rows persisted.",
                userId,
                chatIdValue,
                registrations.Count);
            return Deny(
                $"User {userId} onboarding failed: {ex.Message}. "
                + "All workspace bindings were rolled back atomically; no rows were persisted. "
                + "Common cause: a (OperatorAlias, TenantId) collision with another operator's binding "
                + "(per architecture.md lines 116-119 alias uniqueness).");
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
    /// §7.1: "12345"), falling back to InvariantCulture-formatted
    /// long → string for tolerance against configuration providers
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
    /// the optional leading slash (matches
    /// <see cref="ConfiguredOperatorAuthorizationService.IsStartCommand"/>).
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
