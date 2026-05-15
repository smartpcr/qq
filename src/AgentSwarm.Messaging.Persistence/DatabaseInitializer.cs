using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Initializes the messaging database on application startup.
/// </summary>
/// <remarks>
/// <para>
/// Always applies pending migrations via <see cref="DatabaseFacade.MigrateAsync(System.Threading.CancellationToken)"/>.
/// This is the only schema-evolution-safe path: <c>EnsureCreatedAsync</c>
/// is a one-shot "create if absent" operation that <b>does not add new
/// tables to a pre-existing database</b>, so an existing
/// <c>messaging.db</c> from before the Stage 2.3
/// <c>OutboundMessageIdMapping</c> table was introduced would never
/// receive the new schema and the
/// <c>PersistentMessageIdTracker</c> writes would fail at runtime.
/// </para>
/// <para>
/// Migrations work for both fresh databases (no <c>__EFMigrationsHistory</c>
/// table → all migrations applied) and incremental updates (existing
/// <c>__EFMigrationsHistory</c> → only pending migrations applied), so
/// this single code path covers every deployment scenario.
/// </para>
/// </remarks>
internal sealed class DatabaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var pendingList = pending.ToList();
        if (pendingList.Count > 0)
        {
            _logger.LogInformation(
                "Applying {Count} pending EF Core migration(s) to messaging database: {Migrations}",
                pendingList.Count,
                string.Join(", ", pendingList));
        }

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
