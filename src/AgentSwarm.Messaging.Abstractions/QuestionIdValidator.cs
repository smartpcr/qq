using System.Diagnostics.CodeAnalysis;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Validates <see cref="AgentQuestion.QuestionId"/> values against the cross-connector
/// constraints documented in architecture.md Section 3.1: non-empty, printable ASCII,
/// no control characters, no <c>:</c> separator (reserved by the
/// <c>q:{QuestionId}:{ActionId}</c> component identifier scheme used by Discord and
/// other connectors), and no more than <see cref="MaxLength"/> characters.
/// </summary>
public static class QuestionIdValidator
{
    /// <summary>Maximum allowed length, in characters, of a question id.</summary>
    public const int MaxLength = 30;

    /// <summary>
    /// Attempts to validate <paramref name="questionId"/>. Returns <see langword="true"/>
    /// when the value satisfies every constraint; otherwise returns <see langword="false"/>
    /// and populates <paramref name="error"/> with a human-readable reason.
    /// </summary>
    public static bool TryValidate(
        string? questionId,
        [NotNullWhen(false)] out string? error)
    {
        if (questionId is null)
        {
            error = "QuestionId must not be null.";
            return false;
        }

        if (questionId.Length == 0)
        {
            error = "QuestionId must not be empty.";
            return false;
        }

        if (questionId.Length > MaxLength)
        {
            error =
                $"QuestionId length {questionId.Length} exceeds the maximum of {MaxLength} characters.";
            return false;
        }

        for (var i = 0; i < questionId.Length; i++)
        {
            var c = questionId[i];

            if (c == ':')
            {
                error =
                    $"QuestionId must not contain ':' (reserved separator) at position {i}.";
                return false;
            }

            // Printable ASCII range is 0x20..0x7E inclusive. Anything outside that
            // (control chars, DEL, or non-ASCII) is rejected.
            if (c < 0x20 || c > 0x7E)
            {
                error =
                    $"QuestionId must contain only printable ASCII characters (0x20-0x7E); invalid character 0x{(int)c:X4} at position {i}.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates <paramref name="questionId"/> and throws
    /// <see cref="ArgumentException"/> with a descriptive message when invalid.
    /// </summary>
    public static void EnsureValid(string? questionId, string paramName = "questionId")
    {
        if (!TryValidate(questionId, out var error))
        {
            throw new ArgumentException(error, paramName);
        }
    }
}
