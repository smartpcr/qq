using System.Diagnostics.CodeAnalysis;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Cross-field invariant validator for <see cref="AgentQuestion"/> and
/// <see cref="AgentQuestionEnvelope"/>. Per-field constraints (printable ASCII,
/// length, no <c>:</c>) are owned by <see cref="QuestionIdValidator"/> and
/// <see cref="ActionIdValidator"/>; this validator catches mistakes the per-field
/// validators cannot see:
/// <list type="bullet">
///   <item><description>Duplicate <see cref="HumanAction.ActionId"/> within an
///   <see cref="AgentQuestion.AllowedActions"/> collection -- the connector
///   parser would not know which action a button click resolved to.</description></item>
///   <item><description>
///   <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/> that does not
///   match any <see cref="HumanAction.ActionId"/> in the wrapped question --
///   connectors would highlight a phantom button.</description></item>
/// </list>
/// </summary>
public static class AgentQuestionValidator
{
    /// <summary>
    /// Validates per-field constraints AND cross-field invariants of
    /// <paramref name="question"/>. Returns <see langword="true"/> when every
    /// rule holds; otherwise returns <see langword="false"/> and populates
    /// <paramref name="error"/> with the first failure reason encountered.
    /// </summary>
    public static bool TryValidate(
        AgentQuestion? question,
        [NotNullWhen(false)] out string? error)
    {
        if (question is null)
        {
            error = "AgentQuestion must not be null.";
            return false;
        }

        if (!QuestionIdValidator.TryValidate(question.QuestionId, out error))
        {
            return false;
        }

        if (question.AllowedActions is null)
        {
            error = "AgentQuestion.AllowedActions must not be null.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < question.AllowedActions.Count; i++)
        {
            var action = question.AllowedActions[i];
            if (action is null)
            {
                error =
                    $"AgentQuestion.AllowedActions[{i}] must not be null.";
                return false;
            }

            if (!ActionIdValidator.TryValidate(action.ActionId, out var actionError))
            {
                error = $"AgentQuestion.AllowedActions[{i}].ActionId: {actionError}";
                return false;
            }

            if (!seen.Add(action.ActionId))
            {
                error =
                    $"AgentQuestion.AllowedActions contains duplicate ActionId '{action.ActionId}' at index {i}.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates <paramref name="question"/> and throws
    /// <see cref="ArgumentException"/> with a descriptive message when invalid.
    /// </summary>
    public static void EnsureValid(AgentQuestion? question, string paramName = "question")
    {
        if (!TryValidate(question, out var error))
        {
            throw new ArgumentException(error, paramName);
        }
    }

    /// <summary>
    /// Validates per-field and cross-field invariants of
    /// <paramref name="envelope"/>: every invariant from
    /// <see cref="TryValidate(AgentQuestion?, out string?)"/> AND
    /// <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/> (when non-null)
    /// matches a <see cref="HumanAction.ActionId"/> in the wrapped question.
    /// </summary>
    public static bool TryValidate(
        AgentQuestionEnvelope? envelope,
        [NotNullWhen(false)] out string? error)
    {
        if (envelope is null)
        {
            error = "AgentQuestionEnvelope must not be null.";
            return false;
        }

        if (envelope.RoutingMetadata is null)
        {
            error = "AgentQuestionEnvelope.RoutingMetadata must not be null.";
            return false;
        }

        if (!TryValidate(envelope.Question, out error))
        {
            return false;
        }

        if (envelope.ProposedDefaultActionId is { } defaultId)
        {
            var match = false;
            foreach (var action in envelope.Question.AllowedActions)
            {
                if (string.Equals(action.ActionId, defaultId, StringComparison.Ordinal))
                {
                    match = true;
                    break;
                }
            }

            if (!match)
            {
                error =
                    $"AgentQuestionEnvelope.ProposedDefaultActionId '{defaultId}' is not present in AllowedActions.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates <paramref name="envelope"/> and throws
    /// <see cref="ArgumentException"/> with a descriptive message when invalid.
    /// </summary>
    public static void EnsureValid(AgentQuestionEnvelope? envelope, string paramName = "envelope")
    {
        if (!TryValidate(envelope, out var error))
        {
            throw new ArgumentException(error, paramName);
        }
    }
}
