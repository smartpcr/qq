// -----------------------------------------------------------------------
// <copyright file="QuestionTimeoutOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram;

using System;

/// <summary>
/// Stage 3.5 — configuration for <see cref="QuestionTimeoutService"/>.
/// Bound from the <c>Telegram:QuestionTimeout</c> configuration section
/// in <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Defaults are picked to keep the service responsive without burning
/// query budget on an idle table: a 30-second poll cadence means an
/// expired question fires its default action within at most 30 seconds
/// of the wall-clock expiry; the polling query is index-backed on
/// <c>(Status, ExpiresAt)</c> so even a million-row table reads only
/// the eligible rows.
/// </remarks>
public sealed class QuestionTimeoutOptions
{
    /// <summary>
    /// Configuration section name relative to the <c>Telegram</c>
    /// section root — full key is <c>Telegram:QuestionTimeout</c>.
    /// </summary>
    public const string SectionName = "QuestionTimeout";

    /// <summary>
    /// Interval between successive
    /// <see cref="IPendingQuestionStore.GetExpiredAsync"/> polls.
    /// Default 30 seconds.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
}
