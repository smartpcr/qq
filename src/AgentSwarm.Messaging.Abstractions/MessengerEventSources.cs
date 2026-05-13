namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Canonical values for <see cref="MessengerEvent.Source"/>. Per architecture.md §3.1, the
/// field is nullable: <c>null</c> represents a direct message and <see cref="MessageAction"/>
/// represents a forwarded message-extension submission. The connector implementations are
/// also free to use <see cref="PersonalChat"/> or <see cref="TeamChannel"/> to disambiguate
/// channel origination per implementation-plan.md §1.1.
/// </summary>
public static class MessengerEventSources
{
    /// <summary>Originated in a 1:1 personal chat.</summary>
    public const string PersonalChat = "PersonalChat";

    /// <summary>Originated in a team channel (group conversation).</summary>
    public const string TeamChannel = "TeamChannel";

    /// <summary>Originated from a Teams message-extension submission (<c>composeExtension/submitAction</c>).</summary>
    public const string MessageAction = "MessageAction";

    /// <summary>All canonical source values defined by implementation-plan.md §1.1.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { PersonalChat, TeamChannel, MessageAction };

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is null (direct message) or one of <see cref="All"/>.</summary>
    public static bool IsValid(string? value) => value is null || All.Contains(value);
}
