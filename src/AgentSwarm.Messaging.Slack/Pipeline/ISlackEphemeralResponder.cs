// -----------------------------------------------------------------------
// <copyright file="ISlackEphemeralResponder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Posts an ephemeral text reply back to Slack via the
/// <c>response_url</c> issued with every slash command and interactive
/// payload. Stage 5.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// uses this surface to deliver "unrecognized sub-command" and
/// "missing argument" error messages after the controller has already
/// ACK'd the slash command (implementation step 9).
/// </summary>
/// <remarks>
/// <para>
/// Slack's <c>response_url</c> is a per-invocation URL that accepts a
/// JSON body with <c>response_type</c> and <c>text</c> (plus optional
/// blocks). It stays valid for ~30 minutes after issuance and tolerates
/// up to five POSTs, so an async handler invoked from
/// <see cref="SlackInboundIngestor"/> can still reach the requesting
/// user even though the original HTTP request has long since
/// completed.
/// </para>
/// <para>
/// Production implementations POST to the URL with an
/// <see cref="System.Net.Http.HttpClient"/>; test hosts substitute the
/// <see cref="InMemorySlackEphemeralResponder"/> so assertions can
/// inspect the captured messages without a real network call. The
/// contract is best-effort: implementations MUST NOT throw on
/// transient HTTP failures because the dispatch loop already owns the
/// envelope's terminal disposition and a missed ephemeral message is
/// recoverable from logs.
/// </para>
/// </remarks>
internal interface ISlackEphemeralResponder
{
    /// <summary>
    /// Posts <paramref name="message"/> to <paramref name="responseUrl"/>
    /// as an ephemeral reply visible only to the invoking user.
    /// Implementations swallow non-fatal HTTP errors; they do throw
    /// for <see cref="OperationCanceledException"/> so the dispatch
    /// loop can honour shutdown.
    /// </summary>
    /// <param name="responseUrl">Slack-issued response_url. When
    /// <see langword="null"/> or empty the responder logs and returns
    /// without attempting any HTTP call.</param>
    /// <param name="message">Plain-text body to render. Slack accepts
    /// Markdown-lite (<c>*bold*</c>, <c>`code`</c>, <c>_italic_</c>);
    /// callers are responsible for staying within the 3000-character
    /// limit.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendEphemeralAsync(string? responseUrl, string message, CancellationToken ct);
}
