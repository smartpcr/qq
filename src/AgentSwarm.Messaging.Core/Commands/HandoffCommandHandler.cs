namespace AgentSwarm.Messaging.Core.Commands;

using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles <c>/handoff TASK-ID @operator-alias</c>. Performs the full
/// oversight transfer described in architecture.md §5.5 and tech-spec.md
/// D-4:
/// <list type="number">
///   <item>Validate the slash-command surface (two args, well-formed).</item>
///   <item>Verify the requested task exists via
///         <see cref="ITaskOversightRepository.GetByTaskIdAsync"/>; if
///         not, return <c>"❌ Task TASK-ID not found"</c>.</item>
///   <item>Verify the requesting operator currently holds oversight; if
///         not, return the same NOT-FOUND text (info-leak resistance).</item>
///   <item>Resolve the target alias via
///         <see cref="IOperatorRegistry.GetByAliasAsync"/> using the
///         REQUESTING operator's <see cref="AuthorizedOperator.TenantId"/>
///         so a hostile actor cannot hand off into a different tenant.</item>
///   <item>Upsert the <see cref="TaskOversight"/> row.</item>
///   <item>Enqueue a durable notification to the new owner via
///         <see cref="IOutboundQueue.EnqueueAsync"/> — the existing
///         outbound queue worker handles retry / dead-lettering.</item>
///   <item>Persist an <see cref="AuditEntry"/> capturing
///         <c>(taskId, sourceAlias, targetAlias, timestamp, correlationId)</c>
///         per the brief.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant-scoped alias resolution.</b> The alias resolver is invoked
/// with the requesting operator's tenant id, never the alias's tenant
/// (which we do not know until we resolve it). This satisfies
/// architecture.md lines 116–119 — a UNIQUE constraint on
/// <c>(OperatorAlias, TenantId)</c> means two operators in different
/// tenants may share an alias, and the only safe handoff target is one
/// inside the requester's own tenant.
/// </para>
/// <para>
/// <b>Idempotency.</b> <see cref="ITaskOversightRepository.UpsertAsync"/>
/// is the idempotent boundary — multiple replays of the same handoff
/// command land on the same row, the same audit entry shape, and the
/// same notification text (the outbound queue's idempotency key
/// derivation handles duplicate enqueues). The handler does not need a
/// distinct idempotency check.
/// </para>
/// </remarks>
public sealed class HandoffCommandHandler : ICommandHandler
{
    public const string UsageMessage =
        "Usage: `/handoff TASK-ID @operator-alias`";

    public const string TaskNotFoundTemplate =
        "❌ Task {0} not found";

    public const string OperatorNotFoundTemplate =
        "❌ Operator {0} is not registered in this tenant";

    public const string SenderConfirmationTemplate =
        "✅ Oversight of {0} transferred to {1}";

    public const string TargetNotificationTemplate =
        "🔁 You now have oversight of task {0}, transferred by {1}.";

    public const string AuditAction = "handoff.transferred";

    private readonly ITaskOversightRepository _oversight;
    private readonly IOperatorRegistry _registry;
    private readonly IOutboundQueue _outbound;
    private readonly IAuditLogger _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<HandoffCommandHandler> _logger;

    public HandoffCommandHandler(
        ITaskOversightRepository oversight,
        IOperatorRegistry registry,
        IOutboundQueue outbound,
        IAuditLogger audit,
        TimeProvider time,
        ILogger<HandoffCommandHandler> logger)
    {
        _oversight = oversight ?? throw new ArgumentNullException(nameof(oversight));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string CommandName => TelegramCommands.Handoff;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(@operator);

        if (command.Arguments.Count != 2
            || string.IsNullOrWhiteSpace(command.Arguments[0])
            || string.IsNullOrWhiteSpace(command.Arguments[1]))
        {
            return new CommandResult
            {
                Success = false,
                ResponseText = UsageMessage,
                ErrorCode = "handoff_invalid_usage",
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
        }

        var taskId = command.Arguments[0];
        var targetAlias = command.Arguments[1];

        var existing = await _oversight.GetByTaskIdAsync(taskId, ct).ConfigureAwait(false);
        if (existing is null
            || existing.OperatorBindingId != @operator.OperatorId)
        {
            // Same surface for "task does not exist at all" and "task
            // exists but caller does not currently own oversight" so we
            // do not leak workspace-wide task ids to an unauthorized
            // operator (architecture.md §4.3 info-leak resistance).
            _logger.LogWarning(
                "HandoffCommandHandler rejected — task missing or not owned. TaskId={TaskId} OperatorId={OperatorId} Existing={Existing}",
                taskId,
                @operator.OperatorId,
                existing?.OperatorBindingId);

            return new CommandResult
            {
                Success = false,
                ResponseText = string.Format(CultureInfo.InvariantCulture, TaskNotFoundTemplate, taskId),
                ErrorCode = "handoff_task_not_found",
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
        }

        var target = await _registry
            .GetByAliasAsync(targetAlias, @operator.TenantId, ct)
            .ConfigureAwait(false);
        if (target is null)
        {
            _logger.LogWarning(
                "HandoffCommandHandler rejected — target alias not registered. TargetAlias={TargetAlias} TenantId={TenantId}",
                targetAlias,
                @operator.TenantId);

            return new CommandResult
            {
                Success = false,
                ResponseText = string.Format(
                    CultureInfo.InvariantCulture,
                    OperatorNotFoundTemplate,
                    targetAlias),
                ErrorCode = "handoff_operator_not_found",
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
        }

        var sourceAlias = string.IsNullOrWhiteSpace(@operator.OperatorAlias)
            ? @operator.OperatorId.ToString()
            : @operator.OperatorAlias;
        var now = _time.GetUtcNow();
        var correlationId = Guid.NewGuid().ToString("N");

        var oversight = new TaskOversight
        {
            TaskId = taskId,
            OperatorBindingId = target.Id,
            AssignedAt = now,
            AssignedBy = sourceAlias,
            CorrelationId = correlationId,
        };

        await _oversight.UpsertAsync(oversight, ct).ConfigureAwait(false);

        var targetNotification = new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = $"handoff:{taskId}:{target.Id:N}:{correlationId}",
            ChatId = target.TelegramChatId,
            Payload = string.Format(
                CultureInfo.InvariantCulture,
                TargetNotificationTemplate,
                taskId,
                sourceAlias),
            Severity = MessageSeverity.High,
            SourceType = OutboundSourceType.CommandAck,
            SourceId = taskId,
            CreatedAt = now,
            CorrelationId = correlationId,
        };

        await _outbound.EnqueueAsync(targetNotification, ct).ConfigureAwait(false);

        await _audit.LogAsync(
            new AuditEntry
            {
                EntryId = Guid.NewGuid(),
                MessageId = taskId,
                UserId = sourceAlias,
                AgentId = null,
                Action = AuditAction,
                Timestamp = now,
                CorrelationId = correlationId,
                Details = string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"taskId\":\"{0}\",\"sourceAlias\":\"{1}\",\"targetAlias\":\"{2}\",\"sourceBindingId\":\"{3}\",\"targetBindingId\":\"{4}\"}}",
                    taskId,
                    sourceAlias,
                    targetAlias,
                    @operator.OperatorId,
                    target.Id),
            },
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "HandoffCommandHandler transferred oversight. TaskId={TaskId} SourceAlias={SourceAlias} TargetAlias={TargetAlias} CorrelationId={CorrelationId}",
            taskId,
            sourceAlias,
            targetAlias,
            correlationId);

        return new CommandResult
        {
            Success = true,
            ResponseText = string.Format(
                CultureInfo.InvariantCulture,
                SenderConfirmationTemplate,
                taskId,
                targetAlias),
            CorrelationId = correlationId,
        };
    }
}
