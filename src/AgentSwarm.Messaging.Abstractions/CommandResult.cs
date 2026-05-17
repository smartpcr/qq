namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Structured outcome of a command dispatched through <see cref="ICommandRouter"/>.
/// </summary>
public sealed record CommandResult
{
    private readonly string _correlationId = null!;

    /// <summary>True when the command completed without error.</summary>
    public required bool Success { get; init; }

    /// <summary>Optional reply text to surface to the operator.</summary>
    public string? ResponseText { get; init; }

    /// <summary>
    /// Optional inline-keyboard buttons to render alongside
    /// <see cref="ResponseText"/>. Stage 3.2 command handlers do NOT
    /// own the multi-workspace disambiguation prompt — that responsibility
    /// was moved to <c>TelegramUpdatePipeline.ExecuteAsync</c> (iter-2
    /// evaluator items 1–3) so the durable
    /// <c>ws:&lt;token&gt;:&lt;index&gt;</c> +
    /// <c>PendingDisambiguation</c> callback contract has a single owner;
    /// see <c>TelegramUpdatePipelineTests.Pipeline_Command_WhenMultipleBindings_PromptsWorkspaceSelection_WithInlineKeyboard</c>.
    /// Handlers may still populate this list for their own purposes (e.g.
    /// Stage 3.3 approval/rejection keyboards on agent questions); when
    /// they do, the <see cref="ITelegramUpdatePipeline"/> implementation
    /// copies the list verbatim onto
    /// <see cref="PipelineResult.ResponseButtons"/> so the sender layer
    /// renders a real Telegram inline keyboard alongside the textual
    /// reply. Defaults to an empty list so existing handlers and the
    /// Stage 2.2 stubs continue to return plain-text replies without
    /// modification.
    /// </summary>
    public IReadOnlyList<InlineButton> ResponseButtons { get; init; } = Array.Empty<InlineButton>();

    /// <summary>Trace identifier propagated from the inbound event.</summary>
    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }

    /// <summary>
    /// Machine-readable error code when <see cref="Success"/> is <c>false</c>;
    /// <c>null</c> on successful completion.
    /// </summary>
    public string? ErrorCode { get; init; }
}
