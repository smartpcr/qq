using System.Text.Json.Serialization;
using AgentSwarm.Messaging.Abstractions.Json;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Lifecycle state of a tracked agent question. See architecture.md
/// Section 3.1 (PendingQuestionRecord) and Section 4.7 (IPendingQuestionStore).
/// </summary>
/// <remarks>
/// Wire format: serialized as the member name string via
/// <see cref="PendingQuestionStatusJsonConverter"/> — names-only contract,
/// matching the rest of the shared messenger enums.
/// </remarks>
[JsonConverter(typeof(PendingQuestionStatusJsonConverter))]
public enum PendingQuestionStatus
{
    /// <summary>Posted to the operator; awaiting a button/select-menu response.</summary>
    Pending = 0,

    /// <summary>Operator chose an action; the question is closed.</summary>
    Answered = 1,

    /// <summary>
    /// Operator chose an action whose <see cref="HumanAction.RequiresComment"/>
    /// is <see langword="true"/>; the connector is waiting for the modal /
    /// follow-up reply that carries the rationale.
    /// </summary>
    AwaitingComment = 2,

    /// <summary>The question's <c>ExpiresAt</c> elapsed before resolution.</summary>
    TimedOut = 3,
}
