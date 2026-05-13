namespace Qq.Messaging.Abstractions;

/// <summary>
/// Distinguishes between a slash-command and a button callback response.
/// </summary>
public enum InteractionType
{
    Command = 0,
    CallbackResponse = 1
}
