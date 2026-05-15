using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// Schema-level regression tests for <see cref="TeamsConversationReferenceDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// These tests pin the index set required by the Stage 4.1 implementation-plan §4.1
/// (indexes that callers depend on for FR-006 multi-tenant isolation and FR-007 1000+
/// concurrent-user scaling). If a future edit removes or renames any index declared in
/// <see cref="TeamsConversationReferenceDbContext.OnModelCreating"/> the relevant assert
/// fails immediately, before any consumer notices a missed seek.
/// </para>
/// <para>
/// The migration-drift assertion uses EF's <see cref="IMigrationsModelDiffer"/> to compare
/// the live <see cref="TeamsConversationReferenceDbContext"/> model against the snapshot
/// the latest committed migration produces. Without this guard the
/// <c>IX_ConversationReferences_ConversationId</c> drift that this iteration corrected
/// (the index was added to <c>OnModelCreating</c> in Stage 4.1 but the matching migration
/// was never regenerated) would have shipped silently — production deploys via
/// <c>dotnet ef database update</c> would have built the schema without the hot-path
/// filtered index, while in-process tests using <c>EnsureCreated</c> would still have
/// passed because they read directly from the model.
/// </para>
/// </remarks>
public class TeamsConversationReferenceDbContextSchemaTests
{
    [Fact(DisplayName = "OnModelCreating declares every index required by the Stage 4.1 spec")]
    public void OnModelCreating_DeclaresAllRequiredIndexes()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TeamsConversationReferenceDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new TeamsConversationReferenceDbContext(options);
        var entityType = context.Model.FindEntityType(typeof(ConversationReferenceEntity));
        Assert.NotNull(entityType);

        var indexes = entityType!.GetIndexes().ToDictionary(i => i.GetDatabaseName()!);

        AssertIndex(
            indexes,
            "IX_ConversationReferences_AadObjectId_TenantId",
            new[] { nameof(ConversationReferenceEntity.AadObjectId), nameof(ConversationReferenceEntity.TenantId) },
            isUnique: true,
            filter: "\"AadObjectId\" IS NOT NULL");

        AssertIndex(
            indexes,
            "IX_ConversationReferences_InternalUserId_TenantId",
            new[] { nameof(ConversationReferenceEntity.InternalUserId), nameof(ConversationReferenceEntity.TenantId) },
            isUnique: false,
            filter: "\"InternalUserId\" IS NOT NULL");

        AssertIndex(
            indexes,
            "IX_ConversationReferences_ChannelId_TenantId",
            new[] { nameof(ConversationReferenceEntity.ChannelId), nameof(ConversationReferenceEntity.TenantId) },
            isUnique: true,
            filter: "\"ChannelId\" IS NOT NULL");

        AssertIndex(
            indexes,
            "IX_ConversationReferences_TenantId",
            new[] { nameof(ConversationReferenceEntity.TenantId) },
            isUnique: false,
            filter: null);

        AssertIndex(
            indexes,
            "IX_ConversationReferences_IsActive",
            new[] { nameof(ConversationReferenceEntity.IsActive) },
            isUnique: false,
            filter: "\"IsActive\" = 1");

        AssertIndex(
            indexes,
            "IX_ConversationReferences_ConversationId",
            new[] { nameof(ConversationReferenceEntity.ConversationId) },
            isUnique: false,
            filter: "\"IsActive\" = 1");
    }

    [Fact(DisplayName = "Latest migration snapshot matches the live DbContext model (no drift)")]
    public void MigrationsAreInSyncWithModel()
    {
        // Use the same provider the migration snapshot was generated against (SqlServer
        // — see TeamsConversationReferenceDbContextDesignTimeFactory). Mixing providers
        // produces spurious drift hits because provider-specific annotations
        // (Sql Server identity columns, value-generation strategies, etc.) live on the
        // model. No connection is opened — only the model graph is needed.
        var options = new DbContextOptionsBuilder<TeamsConversationReferenceDbContext>()
            .UseSqlServer("Server=(localdb)\\unused;Database=DriftTest;Trusted_Connection=true")
            .Options;

        using var context = new TeamsConversationReferenceDbContext(options);

        var differ = context.GetService<IMigrationsModelDiffer>();
        var snapshotModel = context.GetService<IMigrationsAssembly>().ModelSnapshot?.Model
            ?? throw new InvalidOperationException("Migrations assembly has no model snapshot.");

        // Snapshot models from EF tooling are not pre-finalized; calling GetRelationalModel
        // on a non-finalized model throws. Run the snapshot through the same conventions
        // pipeline the design-time model uses so the comparison is apples-to-apples.
        if (snapshotModel is IMutableModel mutableSnapshot)
        {
            snapshotModel = context.GetService<IModelRuntimeInitializer>()
                .Initialize((IModel)mutableSnapshot.FinalizeModel(), designTime: true, validationLogger: null);
        }

        var designTimeModel = context.GetService<IDesignTimeModel>().Model;

        var hasDifferences = differ.HasDifferences(
            snapshotModel.GetRelationalModel(),
            designTimeModel.GetRelationalModel());

        Assert.False(
            hasDifferences,
            "TeamsConversationReferenceDbContext model has changes that are not captured in a migration. " +
            "Run `dotnet ef migrations add <Name>` from the AgentSwarm.Messaging.Teams.EntityFrameworkCore " +
            "project to regenerate the snapshot.");
    }

    private static void AssertIndex(
        IReadOnlyDictionary<string, IIndex> indexes,
        string name,
        IReadOnlyList<string> columns,
        bool isUnique,
        string? filter)
    {
        Assert.True(
            indexes.TryGetValue(name, out var index),
            $"Expected EF index '{name}' is missing from the ConversationReferenceEntity model.");

        var actualColumns = index!.Properties.Select(p => p.Name).ToArray();
        Assert.Equal(columns, actualColumns);
        Assert.Equal(isUnique, index.IsUnique);
        Assert.Equal(filter, index.GetFilter());
    }
}
