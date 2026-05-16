// -----------------------------------------------------------------------
// <copyright file="CardActionHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Teams.Cards;

using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Abstractions.Models;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards.Models;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles Adaptive Card invoke actions (approve / reject / escalate / pause / resume / comment)
/// dispatched from the Teams client and persists the resulting <see cref="HumanDecisionEvent"/>.
/// </summary>
public sealed class CardActionHandler
{
    private const string AdaptiveCardInvokeName = "adaptiveCard/action";

    // Bot Framework adaptive-card invoke response content types.
    // See https://learn.microsoft.com/adaptive-cards/authoring-cards/universal-action-model#response-format
    private const string MessageResponseType = "application/vnd.microsoft.activity.message";
    private const string CardResponseType = "application/vnd.microsoft.card.adaptive";
    private const string ErrorResponseType = "application/vnd.microsoft.error";

    private readonly IQuestionStore questionStore;
    private readonly ICardStateStore cardStateStore;
    private readonly IHumanDecisionPublisher decisionPublisher;
    private readonly ICardRenderer cardRenderer;
    private readonly ILogger<CardActionHandler> logger;

    public CardActionHandler(
        IQuestionStore questionStore,
        ICardStateStore cardStateStore,
        IHumanDecisionPublisher decisionPublisher,
        ICardRenderer cardRenderer,
        ILogger<CardActionHandler> logger)
    {
        this.questionStore = questionStore ?? throw new ArgumentNullException(nameof(questionStore));
        this.cardStateStore = cardStateStore ?? throw new ArgumentNullException(nameof(cardStateStore));
        this.decisionPublisher = decisionPublisher ?? throw new ArgumentNullException(nameof(decisionPublisher));
        this.cardRenderer = cardRenderer ?? throw new ArgumentNullException(nameof(cardRenderer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Entry point invoked from the Teams bot adapter when an adaptive-card action arrives.
    /// </summary>
    public async Task<InvokeResponse> HandleAsync(
        ITurnContext<IInvokeActivity> turnContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turnContext);

        var activity = turnContext.Activity;
        if (!string.Equals(activity.Name, AdaptiveCardInvokeName, StringComparison.Ordinal))
        {
            this.logger.LogDebug("Ignoring non-adaptive-card invoke '{Name}'", activity.Name);
            return BuildInvokeResponse(this.Reject("UnsupportedInvoke", "Unsupported invoke activity."));
        }

        CardActionPayload? payload;
        try
        {
            payload = ParsePayload(activity.Value);
        }
        catch (JsonException ex)
        {
            this.logger.LogWarning(ex, "Invalid adaptive-card payload");
            return BuildInvokeResponse(this.Reject("InvalidPayload", "Adaptive card payload was malformed."));
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.QuestionId) || string.IsNullOrWhiteSpace(payload.ActionValue))
        {
            return BuildInvokeResponse(this.Reject("InvalidPayload", "QuestionId and ActionValue are required."));
        }

        var question = await this.questionStore.GetAsync(payload.QuestionId, cancellationToken).ConfigureAwait(false);
        if (question is null)
        {
            this.logger.LogInformation("Question {QuestionId} not found", payload.QuestionId);
            return BuildInvokeResponse(this.Reject("QuestionNotFound", "This question is no longer available."));
        }

        if (question.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return BuildInvokeResponse(this.Reject("QuestionExpired", "This question has expired."));
        }

        var cardState = await this.cardStateStore.GetAsync(payload.QuestionId, cancellationToken).ConfigureAwait(false);
        if (cardState is { Status: CardStatus.Answered })
        {
            return BuildInvokeResponse(this.Reject("AlreadyAnswered", "This question has already been answered."));
        }

        var decision = new HumanDecisionEvent(
            QuestionId: question.QuestionId,
            ActionValue: payload.ActionValue,
            Comment: payload.Comment,
            Messenger: "MicrosoftTeams",
            ExternalUserId: activity.From?.AadObjectId ?? activity.From?.Id ?? "unknown",
            ExternalMessageId: activity.ReplyToId ?? activity.Id ?? Guid.NewGuid().ToString("N"),
            ReceivedAt: DateTimeOffset.UtcNow,
            CorrelationId: question.CorrelationId);

        try
        {
            await this.decisionPublisher.PublishAsync(decision, cancellationToken).ConfigureAwait(false);
            await this.cardStateStore.MarkAnsweredAsync(question.QuestionId, decision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to persist decision for question {QuestionId}", question.QuestionId);
            return BuildInvokeResponse(this.Reject("PersistenceFailure", "We could not record your response. Please try again."));
        }

        var updatedCard = this.cardRenderer.RenderAnswered(question, decision);
        return BuildInvokeResponse(this.Accept(updatedCard));
    }

    // ---- Helpers -----------------------------------------------------------------

    /// <summary>
    /// Builds a successful adaptive-card invoke response. When <paramref name="replacementCard"/> is
    /// provided the Teams client replaces the original card; otherwise a plain message ack is returned.
    /// In both cases the Bot Framework contract requires <c>StatusCode = 200</c>.
    /// </summary>
    private AdaptiveCardInvokeResponse Accept(object? replacementCard = null)
    {
        if (replacementCard is not null)
        {
            return new AdaptiveCardInvokeResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Type = CardResponseType,
                Value = replacementCard,
            };
        }

        return new AdaptiveCardInvokeResponse
        {
            StatusCode = (int)HttpStatusCode.OK,
            Type = MessageResponseType,
            Value = new { message = "Recorded." },
        };
    }

    /// <summary>
    /// Builds a rejection / error adaptive-card invoke response.
    /// Per the Bot Framework Universal Action contract, error responses MUST use a 4xx status code
    /// and the <c>application/vnd.microsoft.error</c> content type so the Teams client renders the
    /// appropriate error UX instead of a green success toast.
    /// </summary>
    private AdaptiveCardInvokeResponse Reject(string code, string message)
    {
        return new AdaptiveCardInvokeResponse
        {
            StatusCode = (int)HttpStatusCode.BadRequest,
            Type = ErrorResponseType,
            Value = new AdaptiveCardInvokeErrorValue
            {
                Code = code,
                Message = message,
            },
        };
    }

    private static InvokeResponse BuildInvokeResponse(AdaptiveCardInvokeResponse body)
    {
        return new InvokeResponse
        {
            Status = body.StatusCode,
            Body = body,
        };
    }

    private static CardActionPayload? ParsePayload(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var json = value is string s ? s : JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<CardActionPayload>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
    }
}
