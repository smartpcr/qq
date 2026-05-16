namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// A single operator-selectable action attached to an <see cref="AgentQuestion"/>.
/// Connectors render <see cref="Label"/> as button text or select-menu option label
/// and encode <see cref="ActionId"/> into the underlying interaction component id
/// (Discord: <c>q:{QuestionId}:{ActionId}</c>). The resolved <see cref="Value"/> is
/// returned to the orchestrator via <see cref="HumanDecisionEvent.ActionValue"/>;
/// any free-form rationale collected when <see cref="RequiresComment"/> is
/// <see langword="true"/> is delivered via <see cref="HumanDecisionEvent.Comment"/>.
/// </summary>
/// <param name="ActionId">
/// Stable identifier unique within an <see cref="AgentQuestion.AllowedActions"/> array.
/// Must satisfy <see cref="ActionIdValidator"/> — printable ASCII, no <c>:</c>
/// separator, no control characters, max <see cref="ActionIdValidator.MaxLength"/>
/// characters — so the composite <c>q:{QuestionId}:{ActionId}</c> remains parseable
/// and fits within Discord's 100-character <c>custom_id</c> limit and Telegram's
/// 64-byte <c>callback_data</c> limit.
/// </param>
/// <param name="Label">Human-visible button or option text.</param>
/// <param name="Value">Logical value carried back to the orchestrator on selection.</param>
/// <param name="RequiresComment">
/// When <see langword="true"/>, the connector must prompt the operator for free-form
/// text before finalising the decision (Discord: a modal; Telegram: a follow-up reply).
/// The collected text is delivered on <see cref="HumanDecisionEvent.Comment"/>.
/// </param>
public sealed record HumanAction(
    string ActionId,
    string Label,
    string Value,
    bool RequiresComment);
