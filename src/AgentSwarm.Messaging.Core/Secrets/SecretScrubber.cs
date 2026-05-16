// -----------------------------------------------------------------------
// <copyright file="SecretScrubber.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

/// <summary>
/// Replaces a secret value with a fixed-shape placeholder so callers
/// can include "something was here" in diagnostic output without
/// leaking the secret material itself.
/// </summary>
/// <remarks>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The placeholder is intentionally NOT the secret length, the first/last
/// characters, or any other partial hint -- architecture.md §7.3 rules
/// out "included in audit entries" for any portion of the secret.
/// </remarks>
public static class SecretScrubber
{
    /// <summary>
    /// Fixed placeholder emitted by <see cref="Scrub"/> when a secret
    /// is present. Exposed as a constant so tests can assert against the
    /// exact literal a scrubbed <c>ToString</c> override emits.
    /// </summary>
    public const string Placeholder = "***";

    /// <summary>
    /// Placeholder emitted when the underlying secret reference resolved
    /// to <see langword="null"/> or an empty string. Kept distinct from
    /// <see cref="Placeholder"/> so an operator triaging "value was
    /// scrubbed" vs. "no value was ever set" can tell them apart in
    /// log output.
    /// </summary>
    public const string EmptyPlaceholder = "(empty)";

    /// <summary>
    /// Returns <see cref="Placeholder"/> when <paramref name="secret"/>
    /// holds any non-empty value; returns <see cref="EmptyPlaceholder"/>
    /// when the secret is <see langword="null"/> or empty.
    /// </summary>
    /// <param name="secret">The secret value to scrub.</param>
    /// <returns>The scrubbed placeholder, never the original value.</returns>
    public static string Scrub(string? secret)
    {
        return string.IsNullOrEmpty(secret) ? EmptyPlaceholder : Placeholder;
    }
}
