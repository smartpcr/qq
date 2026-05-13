namespace Qq.Messaging.Abstractions;

/// <summary>
/// A button that can be displayed to the human operator as part of an agent question.
/// </summary>
public sealed record QuestionButton(
    string CallbackId,
    string Label,
    string Value);
