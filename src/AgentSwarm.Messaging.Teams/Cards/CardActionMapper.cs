using AgentSwarm.Messaging.Abstractions;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Maps an Adaptive Card <c>Action.Submit</c> data payload (carried on
/// <see cref="Microsoft.Bot.Schema.Activity.Value"/>) into a fully-populated
/// <see cref="HumanDecisionEvent"/>. Implements step 6 of <c>implementation-plan.md</c>
/// §3.1: "Create <c>CardActionMapper</c> to map Adaptive Card <c>Action.Submit</c> data
/// payloads back to <see cref="HumanDecisionEvent"/> records."
/// </summary>
/// <remarks>
/// <para>
/// Bot Framework deserialises the inbound activity body using
/// <c>Newtonsoft.Json</c>. <c>Activity.Value</c> therefore arrives as either a
/// <see cref="JObject"/> (most common) or a primitive boxed object, depending on how the
/// upstream serializer decoded it. Both shapes are accepted: <see cref="JObject"/> is read
/// directly, anything else is round-tripped through <c>JObject.FromObject</c>.
/// </para>
/// <para>
/// <b>Comment handling.</b> The <c>comment</c> key is present on the payload only when
/// the originating <see cref="AgentQuestion"/> contained at least one
/// <see cref="HumanAction"/> with <see cref="HumanAction.RequiresComment"/> = <c>true</c>
/// (the renderer adds an <c>Input.Text</c> in that case). Even then the user may submit an
/// empty string when pressing an action button that does not itself require a comment;
/// <see cref="Map"/> normalises empty / whitespace strings to <c>null</c> on the output
/// <see cref="HumanDecisionEvent.Comment"/> so downstream consumers can treat
/// "no comment" uniformly.
/// </para>
/// </remarks>
public sealed class CardActionMapper
{
    /// <summary>
    /// Map a raw Bot Framework <c>Activity.Value</c> payload into a
    /// <see cref="HumanDecisionEvent"/>. The required keys are
    /// <see cref="CardActionDataKeys.QuestionId"/>, <see cref="CardActionDataKeys.ActionId"/>,
    /// <see cref="CardActionDataKeys.ActionValue"/>, and
    /// <see cref="CardActionDataKeys.CorrelationId"/>;
    /// <see cref="CardActionDataKeys.Comment"/> is optional.
    /// </summary>
    /// <remarks>
    /// <see cref="HumanDecisionEvent"/> itself carries only <c>ActionValue</c> (per
    /// <c>HumanDecisionEvent.cs</c>); the <c>ActionId</c> is required on the inbound
    /// payload so Stage 3.3's <c>CardActionHandler</c> can resolve the originating
    /// action button on the stored question (per <c>architecture.md</c> §6.3 step 4),
    /// but it is not propagated onto the decision event. Use <see cref="ReadPayload"/>
    /// directly when you need access to the <c>ActionId</c>.
    /// </remarks>
    /// <param name="activityValue">The raw <c>Activity.Value</c> from the inbound invoke.</param>
    /// <param name="messenger">Source messenger label (Teams).</param>
    /// <param name="externalUserId">External user identifier (AAD object ID for Teams).</param>
    /// <param name="externalMessageId">External activity / message ID of the user's response.</param>
    /// <param name="receivedAt">UTC time the gateway received the response.</param>
    /// <returns>A fully-populated <see cref="HumanDecisionEvent"/>.</returns>
    /// <exception cref="ArgumentNullException">Any required reference argument is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The payload is missing one of the required keys or carries a blank value.</exception>
    public HumanDecisionEvent Map(
        object activityValue,
        string messenger,
        string externalUserId,
        string externalMessageId,
        DateTimeOffset receivedAt)
    {
        if (activityValue is null)
        {
            throw new ArgumentNullException(nameof(activityValue));
        }

        if (string.IsNullOrWhiteSpace(messenger))
        {
            throw new ArgumentNullException(nameof(messenger));
        }

        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            throw new ArgumentNullException(nameof(externalUserId));
        }

        if (string.IsNullOrWhiteSpace(externalMessageId))
        {
            throw new ArgumentNullException(nameof(externalMessageId));
        }

        var payload = ToJObject(activityValue);

        var questionId = ReadRequiredString(payload, CardActionDataKeys.QuestionId);
        // ActionId is consumed by Stage 3.3's CardActionHandler for unambiguous button
        // resolution (per architecture.md §6.3 step 4) — its presence is enforced here
        // even though it is not copied onto HumanDecisionEvent.
        _ = ReadRequiredString(payload, CardActionDataKeys.ActionId);
        var actionValue = ReadRequiredString(payload, CardActionDataKeys.ActionValue);
        var correlationId = ReadRequiredString(payload, CardActionDataKeys.CorrelationId);
        var comment = ReadOptionalString(payload, CardActionDataKeys.Comment);

        return new HumanDecisionEvent(
            QuestionId: questionId,
            ActionValue: actionValue,
            Comment: comment,
            Messenger: messenger,
            ExternalUserId: externalUserId,
            ExternalMessageId: externalMessageId,
            ReceivedAt: receivedAt,
            CorrelationId: correlationId);
    }

    /// <summary>
    /// Deserialise the strongly-typed <see cref="CardActionPayload"/> view of an
    /// inbound <c>Activity.Value</c>. Useful for code paths that need access to the raw
    /// fields (including the <see cref="CardActionDataKeys.ActionId"/> for unambiguous
    /// button-identity resolution per <c>architecture.md</c> §2.10) without producing a
    /// <see cref="HumanDecisionEvent"/> — for example, a debugger / audit logger that
    /// wants to log the payload before any validation, or Stage 3.3's
    /// <c>CardActionHandler</c> that resolves the originating action by
    /// <c>QuestionId + ActionId</c> before emitting the decision event.
    /// </summary>
    public CardActionPayload ReadPayload(object activityValue)
    {
        if (activityValue is null)
        {
            throw new ArgumentNullException(nameof(activityValue));
        }

        var payload = ToJObject(activityValue);
        return new CardActionPayload(
            QuestionId: ReadRequiredString(payload, CardActionDataKeys.QuestionId),
            ActionId: ReadRequiredString(payload, CardActionDataKeys.ActionId),
            ActionValue: ReadRequiredString(payload, CardActionDataKeys.ActionValue),
            CorrelationId: ReadRequiredString(payload, CardActionDataKeys.CorrelationId),
            Comment: ReadOptionalString(payload, CardActionDataKeys.Comment));
    }

    private static JObject ToJObject(object activityValue)
    {
        return activityValue switch
        {
            JObject obj => obj,
            _ => JObject.FromObject(activityValue),
        };
    }

    private static string ReadRequiredString(JObject payload, string key)
    {
        var token = payload[key];
        if (token is null || token.Type == JTokenType.Null)
        {
            throw new InvalidOperationException(
                $"Adaptive Card action payload is missing required key '{key}'. " +
                $"Available keys: [{string.Join(", ", payload.Properties().Select(p => p.Name))}].");
        }

        var value = token.Type == JTokenType.String ? (string?)token : token.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Adaptive Card action payload key '{key}' is empty.");
        }

        return value!;
    }

    private static string? ReadOptionalString(JObject payload, string key)
    {
        var token = payload[key];
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }

        var value = token.Type == JTokenType.String ? (string?)token : token.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
