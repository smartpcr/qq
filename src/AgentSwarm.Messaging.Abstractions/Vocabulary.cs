namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Canonical <see cref="MessengerEvent.EventType"/> discriminator values shared across
/// the swarm. Aligned with the architecture document §3.1 cross-doc alignment note and
/// the e2e-scenarios correlation table.
/// </summary>
public static class MessengerEventTypes
{
    public const string AgentTaskRequest = "AgentTaskRequest";
    public const string Command = "Command";
    public const string Escalation = "Escalation";
    public const string PauseAgent = "PauseAgent";
    public const string ResumeAgent = "ResumeAgent";
    public const string Decision = "Decision";
    public const string Text = "Text";
    public const string InstallUpdate = "InstallUpdate";
    public const string Reaction = "Reaction";
}

/// <summary>
/// Canonical <see cref="MessengerEvent.Source"/> origination-channel values.
/// </summary>
public static class MessengerEventSources
{
    public const string PersonalChat = "PersonalChat";
    public const string TeamChannel = "TeamChannel";
    public const string MessageAction = "MessageAction";
}

/// <summary>
/// Lifecycle status values for <see cref="AgentQuestion.Status"/>. Default is
/// <see cref="Open"/>; transitions to <see cref="Resolved"/> or <see cref="Expired"/>
/// are atomic via <c>IAgentQuestionStore.TryUpdateStatusAsync</c> in later stages.
/// </summary>
public static class AgentQuestionStatuses
{
    public const string Open = "Open";
    public const string Resolved = "Resolved";
    public const string Expired = "Expired";
}

/// <summary>
/// Severity vocabulary shared by <see cref="MessengerMessage.Severity"/> and
/// <see cref="AgentQuestion.Severity"/>.
/// </summary>
public static class MessageSeverities
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Critical = "Critical";
}
