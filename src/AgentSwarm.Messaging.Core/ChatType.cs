namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Coarse classification of the Telegram chat hosting an operator binding,
/// derived from <c>Update.Message.Chat.Type</c>. Used to ensure private-chat
/// commands are not accepted from group/supergroup contexts where multiple
/// users could impersonate one another.
/// </summary>
public enum ChatType
{
    Private,
    Group,
    Supergroup
}
