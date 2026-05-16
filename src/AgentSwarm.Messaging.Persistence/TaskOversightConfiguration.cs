// -----------------------------------------------------------------------
// <copyright file="TaskOversightConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// EF Core configuration for <see cref="TaskOversight"/> — the
/// task-to-operator oversight assignment table backing
/// <c>/handoff</c> (Stage 3.2) and the swarm-event subscription
/// service's status-update / alert routing (Stage 2.7). One row per
/// task. Persisted in the <c>task_oversights</c> SQLite table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Primary key.</b> <see cref="TaskOversight.TaskId"/> is the PK —
/// each task has exactly one current owner. Handoffs are a row UPDATE
/// (or upsert on a never-overseen task), not an INSERT of a second row;
/// the historical "who used to own this task" trail is recorded
/// separately in the audit log (the brief mandates a per-handoff
/// audit entry with source / target / correlation id).
/// </para>
/// <para>
/// <b>Indexes.</b> Two non-unique secondary indexes match the two query
/// shapes the brief names: <c>ix_task_oversights_operator</c> on
/// <see cref="TaskOversight.OperatorBindingId"/> for operator-scoped
/// lookups (<c>/status</c> render of operator-owned tasks per
/// <see cref="ITaskOversightRepository.GetByOperatorAsync"/>), and
/// <c>ix_task_oversights_task_id</c> on
/// <see cref="TaskOversight.TaskId"/>. The TaskId index is logically
/// redundant with the PK on SQLite (PKs are themselves indexed), but
/// the brief explicitly requires "indexes on OperatorBindingId for
/// operator-scoped queries and on TaskId for task lookup" — pinning a
/// named index satisfies that requirement explicitly and makes the
/// intent legible to future readers.
/// </para>
/// <para>
/// <b>Time encoding.</b> <see cref="TaskOversight.AssignedAt"/> stores
/// as Unix milliseconds via the same <see cref="ValueConverter{TModel, TProvider}"/>
/// pattern <see cref="OutboundDeadLetterConfiguration"/> uses, so the
/// representation is consistent across every messenger table.
/// </para>
/// </remarks>
public sealed class TaskOversightConfiguration : IEntityTypeConfiguration<TaskOversight>
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillis =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    public void Configure(EntityTypeBuilder<TaskOversight> builder)
    {
        builder.ToTable("task_oversights");

        builder.HasKey(x => x.TaskId);

        builder.Property(x => x.TaskId)
            .HasMaxLength(128)
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(x => x.OperatorBindingId)
            .IsRequired();

        builder.Property(x => x.AssignedAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        builder.Property(x => x.AssignedBy)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.HasIndex(x => x.OperatorBindingId)
            .HasDatabaseName("ix_task_oversights_operator");

        builder.HasIndex(x => x.TaskId)
            .HasDatabaseName("ix_task_oversights_task_id");
    }
}
