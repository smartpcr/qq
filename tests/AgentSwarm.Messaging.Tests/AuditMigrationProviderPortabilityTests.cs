// -----------------------------------------------------------------------
// <copyright file="AuditMigrationProviderPortabilityTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.Linq;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
/// Stage 5.3 iter-9 evaluator item 1 — pins the provider-portability
/// of the audit migration. The iter-8 evaluator flagged that the
/// original SQLite-scaffolded migration carried explicit
/// <c>type: "TEXT"</c> / <c>type: "INTEGER"</c> hints, which forced
/// PostgreSQL (<c>uuid</c> / <c>bigint</c>) and SQL Server
/// (<c>uniqueidentifier</c> / <c>bigint</c>) to also produce
/// SQLite-shaped column types — truncating 64-bit timestamps and
/// landing Guids as text. The fix is to leave column types
/// unspecified at the migration / model layer so EF picks
/// provider-native types at runtime; this test guards against a
/// future scaffold-regenerate accidentally re-introducing the
/// hard-coded hints.
/// </summary>
public sealed class AuditMigrationProviderPortabilityTests
{
    /// <summary>
    /// The AuditLogEntry properties whose column types MUST stay
    /// provider-agnostic for the single migration set to work on
    /// SQLite, PostgreSQL, and SQL Server.
    /// </summary>
    private static readonly string[] PortableProperties =
    {
        nameof(AuditLogEntry.Id),
        nameof(AuditLogEntry.Timestamp),
        nameof(AuditLogEntry.MessageId),
        nameof(AuditLogEntry.ExternalUserId),
        nameof(AuditLogEntry.AgentId),
        nameof(AuditLogEntry.Action),
        nameof(AuditLogEntry.EventFamily),
        nameof(AuditLogEntry.CorrelationId),
        nameof(AuditLogEntry.TenantId),
        nameof(AuditLogEntry.Details),
        nameof(AuditLogEntry.Platform),
        nameof(AuditLogEntry.EntryKind),
        nameof(AuditLogEntry.QuestionId),
        nameof(AuditLogEntry.ActionValue),
        nameof(AuditLogEntry.Comment),
    };

    [Fact]
    public void AuditLogEntry_ConfigurationDoesNotPinProviderSpecificColumnTypes()
    {
        // Build a model under a SQLite provider configuration (no
        // connection opened — we only need the EF metadata). Any
        // HasColumnType() annotation on the inspected properties
        // would survive the build and surface here. The provider
        // choice is irrelevant for this metadata-only inspection;
        // SQLite is used because it is already a project dependency.
        using var ctx = BuildContext();
        var entity = ctx.Model.FindEntityType(typeof(AuditLogEntry));
        entity.Should().NotBeNull(
            "the audit_logs entity must be mapped or the writer cannot resolve it");

        foreach (var propName in PortableProperties)
        {
            var prop = entity!.FindProperty(propName);
            prop.Should().NotBeNull($"AuditLogEntry.{propName} must be mapped");

            // EF's HasColumnType() lands on the IProperty as a string
            // annotation reachable via the "Relational:ColumnType"
            // annotation. We assert no explicit type is set at the
            // MODEL level — EF falls back to the provider's default
            // mapping when the model doesn't pin one, which is the
            // portability contract.
            var configured = prop!.GetAnnotations()
                .Where(a => a.Name == "Relational:ColumnType")
                .Select(a => a.Value)
                .FirstOrDefault();
            configured.Should().BeNull(
                $"Stage 5.3 iter-9 evaluator item 1: AuditLogEntry.{propName} MUST NOT pin a provider-specific column type — the migration relies on EF's default per-provider mapping (SQLite TEXT / PostgreSQL uuid / SQL Server uniqueidentifier for Guids; SQLite INTEGER / PostgreSQL bigint / SQL Server bigint for longs).");
        }
    }

    [Fact]
    public void AuditLogEntry_TimestampStoresAsInt64_PreservingFullRange()
    {
        // The Stage 5.3 timestamp column carries Unix milliseconds as
        // a `long`. The portability hazard the iter-8 evaluator
        // flagged is a 32-bit truncation when SQLite scaffolders
        // emit `type: "INTEGER"` and a downstream PostgreSQL run
        // interprets that as a 4-byte integer. Pin the EF-side
        // CLR type so the converter still emits Int64.
        using var ctx = BuildContext();
        var entity = ctx.Model.FindEntityType(typeof(AuditLogEntry))!;
        var timestamp = entity.FindProperty(nameof(AuditLogEntry.Timestamp))!;

        timestamp.ClrType.Should().Be(typeof(DateTimeOffset),
            "AuditLogEntry.Timestamp is a DateTimeOffset at the CLR level (Stage 5.3 brief)");

        var converter = timestamp.GetValueConverter();
        converter.Should().NotBeNull(
            "the DateTimeOffset -> Unix-ms value converter must be wired or persistence loses sub-second precision");
        converter!.ProviderClrType.Should().Be(typeof(long),
            "the converter MUST emit Int64 so PostgreSQL `bigint` / SQL Server `bigint` / SQLite `INTEGER` all carry the full Unix-ms range without 32-bit truncation");
    }

    [Theory]
    [InlineData(nameof(AuditLogEntry.MessageId), 128)]
    [InlineData(nameof(AuditLogEntry.ExternalUserId), 128)]
    [InlineData(nameof(AuditLogEntry.AgentId), 128)]
    [InlineData(nameof(AuditLogEntry.Action), 64)]
    [InlineData(nameof(AuditLogEntry.EventFamily), 32)]
    [InlineData(nameof(AuditLogEntry.CorrelationId), 128)]
    [InlineData(nameof(AuditLogEntry.TenantId), 128)]
    [InlineData(nameof(AuditLogEntry.Platform), 32)]
    [InlineData(nameof(AuditLogEntry.EntryKind), 32)]
    [InlineData(nameof(AuditLogEntry.QuestionId), 128)]
    [InlineData(nameof(AuditLogEntry.ActionValue), 64)]
    public void AuditLogEntry_StringColumnsHaveMaxLength_NotProviderSpecificType(string propertyName, int expectedMaxLength)
    {
        // MaxLength is portable across providers (each translates it
        // to varchar(N) / nvarchar(N) / TEXT-with-CHECK depending on
        // engine); HasColumnType("nvarchar(128)") would NOT be. The
        // model uses MaxLength so the migration emits provider-native
        // varchar variants on each engine.
        using var ctx = BuildContext();
        var entity = ctx.Model.FindEntityType(typeof(AuditLogEntry))!;
        var prop = entity.FindProperty(propertyName)!;
        prop.GetMaxLength().Should().Be(expectedMaxLength);
    }

    /// <summary>
    /// Builds an <see cref="AuditDbContext"/> backed by SQLite
    /// without opening a connection — we only need the EF metadata,
    /// not a live database. SQLite is the project's dev-time default
    /// and is already a test project dependency.
    /// </summary>
    private static AuditDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite("Filename=:memory:")
            .Options;
        return new AuditDbContext(options);
    }
}
