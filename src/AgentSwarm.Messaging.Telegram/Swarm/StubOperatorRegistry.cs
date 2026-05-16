using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Telegram.Swarm;

/// <summary>
/// Stage 2.7 dev/test stub <see cref="IOperatorRegistry"/> projected from
/// <see cref="TelegramOptions.DevOperators"/>. Every entry is materialised
/// into an <see cref="OperatorBinding"/> with a deterministic id; the
/// concrete <c>PersistentOperatorRegistry</c> (Stage 3.4) supersedes this
/// stub via <c>AddSingleton</c> last-wins registration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacement contract (architecture.md §4.6 / implementation-plan.md
/// Stage 2.7).</b> Registered in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> via
/// <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}"/>
/// so a later <c>AddSingleton&lt;IOperatorRegistry, PersistentOperatorRegistry&gt;</c>
/// call (Stage 3.4) wins by last-wins semantics.
/// <b>Production-readiness gate.</b> The Phase 6.3 DI wiring is required to
/// register the persistent implementation; the corresponding startup health
/// check asserts that the resolved <see cref="IOperatorRegistry"/> is NOT
/// <see cref="StubOperatorRegistry"/> when <c>ASPNETCORE_ENVIRONMENT=Production</c>.
/// </para>
/// <para>
/// <b>Deterministic binding ids.</b> The stub derives
/// <see cref="OperatorBinding.Id"/> from a SHA-256 hash over
/// <c>"StubOperatorRegistry:{tenantId}:{workspaceId}:{userId}:{chatId}"</c>
/// so the same configured entry always projects the same
/// <see cref="OperatorBinding.Id"/>. This matters for acceptance-test
/// fixtures that join a hand-rolled
/// <c>TaskOversight</c> record (with
/// <c>OperatorBindingId</c> set to the binding id) to the alert routing
/// path inside <c>SwarmEventSubscriptionService</c> — without a stable
/// id, the test would have to inject a custom registry rather than just
/// pre-populating <c>TelegramOptions.DevOperators</c>.
/// </para>
/// <para>
/// <b>Deterministic <see cref="OperatorBinding.RegisteredAt"/>.</b> The
/// stub also stamps <see cref="OperatorBinding.RegisteredAt"/> with a
/// fixed sentinel (<see cref="DateTimeOffset.UnixEpoch"/>) rather than
/// <c>DateTimeOffset.UtcNow</c>. The configuration-backed projection has
/// no notion of "when /start ran" (that field only carries real meaning
/// for the Stage 3.4 <c>PersistentOperatorRegistry</c>), and using
/// <c>UtcNow</c> would make successive reads of the same logical binding
/// return different <see cref="OperatorBinding.RegisteredAt"/> values —
/// breaking consumers that compare, sort, or cache by it. The epoch
/// sentinel keeps the projection stable across calls and signals "not a
/// real registration time" to anyone inspecting a stub binding.
/// </para>
/// <para>
/// <b>IOptionsMonitor.</b> The stub reads
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> on every call so
/// that <c>DevOperators</c> reloads (e.g. from a test
/// <c>WebApplicationFactory.ConfigureServices</c> override) are picked up
/// without restarting the host.
/// </para>
/// </remarks>
public sealed class StubOperatorRegistry : IOperatorRegistry
{
    private readonly IOptionsMonitor<TelegramOptions> _options;

    public StubOperatorRegistry(IOptionsMonitor<TelegramOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OperatorBinding>> GetBindingsAsync(
        long telegramUserId,
        long chatId,
        CancellationToken ct)
    {
        var matches = MaterialiseBindings()
            .Where(b => b.TelegramUserId == telegramUserId && b.TelegramChatId == chatId)
            .ToList();
        return Task.FromResult<IReadOnlyList<OperatorBinding>>(matches);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OperatorBinding>> GetAllBindingsAsync(
        long telegramUserId,
        CancellationToken ct)
    {
        var matches = MaterialiseBindings()
            .Where(b => b.TelegramUserId == telegramUserId)
            .ToList();
        return Task.FromResult<IReadOnlyList<OperatorBinding>>(matches);
    }

    /// <inheritdoc />
    public Task<OperatorBinding?> GetByAliasAsync(
        string operatorAlias,
        string tenantId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var match = MaterialiseBindings()
            .FirstOrDefault(b =>
                string.Equals(b.OperatorAlias, operatorAlias, StringComparison.Ordinal)
                && string.Equals(b.TenantId, tenantId, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OperatorBinding>> GetByWorkspaceAsync(
        string workspaceId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var matches = MaterialiseBindings()
            .Where(b => string.Equals(b.WorkspaceId, workspaceId, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult<IReadOnlyList<OperatorBinding>>(matches);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The stub is read-only: writes to a configuration-backed source are
    /// rejected loudly so callers that attempt to mutate the stub at
    /// runtime (e.g. by calling the <c>/start</c> onboarding handler
    /// before Stage 3.4 wires the persistent registry) fail-fast at the
    /// boundary instead of silently no-oping.
    /// </remarks>
    public Task RegisterAsync(OperatorRegistration registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);
        throw new InvalidOperationException(
            $"{nameof(StubOperatorRegistry)} is a read-only dev/test stub backed by "
            + $"{nameof(TelegramOptions)}.{nameof(TelegramOptions.DevOperators)} configuration. "
            + "Register the Stage 3.4 PersistentOperatorRegistry via AddSingleton to enable RegisterAsync.");
    }

    /// <inheritdoc />
    public Task<bool> IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken ct)
    {
        var authorized = MaterialiseBindings()
            .Any(b => b.TelegramUserId == telegramUserId && b.TelegramChatId == chatId);
        return Task.FromResult(authorized);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetActiveTenantsAsync(CancellationToken ct)
    {
        var tenants = MaterialiseBindings()
            .Select(b => b.TenantId)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(tenants);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OperatorBinding>> GetByTenantAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var matches = MaterialiseBindings()
            .Where(b => string.Equals(b.TenantId, tenantId, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult<IReadOnlyList<OperatorBinding>>(matches);
    }

    /// <summary>
    /// Project the current <see cref="TelegramOptions.DevOperators"/>
    /// snapshot into <see cref="OperatorBinding"/> records. Entries with
    /// a blank <see cref="TelegramOperatorBindingOptions.TenantId"/> or
    /// <see cref="TelegramOperatorBindingOptions.WorkspaceId"/> are
    /// skipped (the startup validator already rejects them; this is the
    /// runtime safety-net for direct mutation of the options object).
    /// </summary>
    private IEnumerable<OperatorBinding> MaterialiseBindings()
    {
        var current = _options.CurrentValue;
        var devOperators = current.DevOperators;
        if (devOperators is null || devOperators.Count == 0)
        {
            yield break;
        }

        foreach (var entry in devOperators)
        {
            if (entry is null) { continue; }
            if (string.IsNullOrWhiteSpace(entry.TenantId) || string.IsNullOrWhiteSpace(entry.WorkspaceId))
            {
                continue;
            }

            var alias = string.IsNullOrWhiteSpace(entry.OperatorAlias)
                ? $"user-{entry.TelegramUserId.ToString(CultureInfo.InvariantCulture)}"
                : entry.OperatorAlias!;

            var roles = entry.Roles is null
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : entry.Roles.ToArray();

            yield return new OperatorBinding
            {
                Id = DeriveBindingId(entry.TelegramUserId, entry.TelegramChatId, entry.TenantId, entry.WorkspaceId),
                TelegramUserId = entry.TelegramUserId,
                TelegramChatId = entry.TelegramChatId,
                ChatType = ChatType.Private,
                OperatorAlias = alias,
                TenantId = entry.TenantId,
                WorkspaceId = entry.WorkspaceId,
                Roles = roles,
                RegisteredAt = DateTimeOffset.UnixEpoch,
                IsActive = true,
            };
        }
    }

    /// <summary>
    /// Deterministic <see cref="OperatorBinding.Id"/> derivation used by
    /// the stub. Mirrors the convention in
    /// <see cref="Auth.ConfiguredOperatorAuthorizationService"/> so a
    /// dev fixture that wires the same (user, chat, tenant, workspace)
    /// across both surfaces sees the same id and can join records
    /// across them (e.g. a TaskOversight row keyed off the alias-derived
    /// binding id).
    /// </summary>
    public static Guid DeriveBindingId(long userId, long chatId, string tenantId, string workspaceId)
    {
        var key = "StubOperatorRegistry:"
            + userId.ToString(CultureInfo.InvariantCulture)
            + ":"
            + chatId.ToString(CultureInfo.InvariantCulture)
            + ":"
            + (tenantId ?? string.Empty)
            + ":"
            + (workspaceId ?? string.Empty);
        var bytes = Encoding.UTF8.GetBytes(key);
        var hash = SHA256.HashData(bytes);
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }
}
