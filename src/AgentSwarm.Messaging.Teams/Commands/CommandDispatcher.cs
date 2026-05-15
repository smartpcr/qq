using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Concrete <see cref="ICommandDispatcher"/> implementation owning Stage 3.2 dispatch
/// responsibilities (per <c>implementation-plan.md</c> §3.2 step 6):
/// </summary>
/// <remarks>
/// <para>
/// <b>Responsibility split.</b> The dispatcher
/// <list type="number">
/// <item><description>parses <see cref="CommandContext.NormalizedText"/> to identify the
/// canonical command keyword (longest-prefix match against
/// <see cref="CommandNames.All"/>, case-insensitive on word boundaries);</description></item>
/// <item><description>resolves the matching <see cref="ICommandHandler"/> from the
/// constructor-supplied collection (DI-registered handlers);</description></item>
/// <item><description>populates <see cref="CommandContext.CommandArguments"/> with the
/// trimmed text remaining after the matched keyword and invokes the handler;</description></item>
/// <item><description>for unrecognized free-text input, publishes a
/// <see cref="TextEvent"/> with the raw input as payload via the injected
/// <see cref="IInboundEventPublisher"/> (per <c>architecture.md</c> §3.1 <c>TextEvent</c>
/// row and <c>e2e-scenarios.md</c> §Unrecognized input) and sends a help card.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>No mention stripping.</b> Per <c>implementation-plan.md</c> §3.2 step 6, the
/// dispatcher MUST NOT call <c>Activity.RemoveMentionText</c>. All <c>@mention</c>
/// stripping is performed by <see cref="TeamsSwarmActivityHandler.OnMessageActivityAsync"/>
/// (Stage 2.2), which is the sole call site of <c>Activity.RemoveRecipientMention</c>.
/// The dispatcher operates exclusively on the pre-cleaned
/// <see cref="CommandContext.NormalizedText"/>.
/// </para>
/// <para>
/// <b>Handler registration.</b> Each <see cref="ICommandHandler"/> registered in DI must
/// expose a unique, case-insensitive <see cref="ICommandHandler.CommandName"/>. The
/// dispatcher constructor enforces this and throws
/// <see cref="ArgumentException"/> on duplicates so misconfiguration is caught at startup
/// rather than at the first inbound activity.
/// </para>
/// </remarks>
public sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IReadOnlyDictionary<string, ICommandHandler> _handlersByName;
    private readonly IReadOnlyList<string> _orderedCommandNames;
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly ILogger<CommandDispatcher> _logger;

    /// <summary>
    /// Construct the dispatcher with the DI-supplied handler collection. Validates that
    /// every handler exposes a non-empty, unique <see cref="ICommandHandler.CommandName"/>;
    /// duplicates throw <see cref="ArgumentException"/> at construction time so the
    /// failure surfaces at startup.
    /// </summary>
    /// <param name="handlers">The set of command handlers discovered via DI. Required.</param>
    /// <param name="inboundEventPublisher">Publisher used to enqueue <see cref="TextEvent"/> records for unrecognized free-text input. Required.</param>
    /// <param name="logger">Structured logger used for command-routing observability. Required.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A handler has an empty / duplicate <see cref="ICommandHandler.CommandName"/>.</exception>
    public CommandDispatcher(
        IEnumerable<ICommandHandler> handlers,
        IInboundEventPublisher inboundEventPublisher,
        ILogger<CommandDispatcher> logger)
    {
        if (handlers is null)
        {
            throw new ArgumentNullException(nameof(handlers));
        }

        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Normalise the registered handler names to lower-case (the canonical form in
        // CommandNames) and reject duplicates. Empty names are rejected because the
        // longest-prefix loop in DispatchAsync would otherwise match every input.
        var byName = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (handler is null)
            {
                throw new ArgumentException("Handler collection contains a null entry.", nameof(handlers));
            }

            var name = handler.CommandName;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    $"Handler '{handler.GetType().FullName}' has an empty CommandName.",
                    nameof(handlers));
            }

            if (byName.ContainsKey(name))
            {
                throw new ArgumentException(
                    $"Duplicate ICommandHandler.CommandName '{name}' registered (existing: " +
                    $"{byName[name].GetType().FullName}, conflicting: {handler.GetType().FullName}).",
                    nameof(handlers));
            }

            byName.Add(name, handler);
        }

        _handlersByName = byName;
        // Order by descending length so the dispatcher prefers "agent ask" over "ask"
        // when both are registered. Secondary order is alphabetical for deterministic
        // tie-breaks between handlers of equal length.
        _orderedCommandNames = byName.Keys
            .OrderByDescending(n => n.Length)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task DispatchAsync(CommandContext context, CancellationToken ct)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var rawText = context.NormalizedText ?? string.Empty;
        var trimmed = rawText.Trim();

        var match = MatchHandler(trimmed);
        if (match is null)
        {
            _logger.LogInformation(
                "Dispatcher received unrecognised input (correlation {CorrelationId}, length {Length}) — publishing TextEvent and replying with help card.",
                context.CorrelationId,
                trimmed.Length);

            await PublishTextEventAsync(context, trimmed, ct).ConfigureAwait(false);
            await SendReplyAsync(context, CommandReplyCards.BuildHelpCard(), ct).ConfigureAwait(false);
            return;
        }

        var (handler, arguments) = match.Value;
        var routed = context with { CommandArguments = arguments };

        _logger.LogInformation(
            "Dispatching command '{CommandName}' to handler '{HandlerType}' (correlation {CorrelationId}, args length {ArgsLength}).",
            handler.CommandName,
            handler.GetType().FullName,
            context.CorrelationId,
            arguments.Length);

        await handler.HandleAsync(routed, ct).ConfigureAwait(false);
    }

    private (ICommandHandler Handler, string Arguments)? MatchHandler(string trimmed)
    {
        if (trimmed.Length == 0)
        {
            return null;
        }

        foreach (var name in _orderedCommandNames)
        {
            // Boundary-safe matching: the candidate name must occupy the whole input or
            // be followed by whitespace. Without this guard, an input like "approveX"
            // would incorrectly match the "approve" handler.
            if (trimmed.Length == name.Length)
            {
                if (trimmed.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return (_handlersByName[name], string.Empty);
                }
            }
            else if (trimmed.Length > name.Length
                && char.IsWhiteSpace(trimmed[name.Length])
                && trimmed.AsSpan(0, name.Length).Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                var args = trimmed[(name.Length + 1)..].TrimStart();
                return (_handlersByName[name], args);
            }
        }

        return null;
    }

    private Task PublishTextEventAsync(CommandContext context, string rawText, CancellationToken ct)
    {
        var textEvent = new TextEvent
        {
            EventId = Guid.NewGuid().ToString(),
            CorrelationId = NonEmpty(context.CorrelationId),
            Messenger = "Teams",
            ExternalUserId = context.ResolvedIdentity?.AadObjectId ?? string.Empty,
            ActivityId = context.ActivityId,
            Source = null,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = rawText,
        };

        return _inboundEventPublisher.PublishAsync(textEvent, ct);
    }

    private static Task SendReplyAsync(CommandContext context, Microsoft.Bot.Schema.IMessageActivity reply, CancellationToken ct)
    {
        if (context.TurnContext is ITurnContext turnContext)
        {
            return turnContext.SendActivityAsync(reply, ct);
        }

        // No turn context attached (unit-test scenarios sometimes omit it). Drop the
        // reply silently — the upstream caller is responsible for asserting reply
        // behaviour via a real turn context.
        return Task.CompletedTask;
    }

    private static string NonEmpty(string? value)
        => string.IsNullOrEmpty(value) ? Guid.NewGuid().ToString() : value;

    // Used only by tests asserting the dispatcher's parsing behaviour.
    internal static string FormatForDiagnostics(CommandContext context)
        => string.Format(
            CultureInfo.InvariantCulture,
            "NormalizedText='{0}', CommandArguments='{1}'",
            context.NormalizedText,
            context.CommandArguments);
}
