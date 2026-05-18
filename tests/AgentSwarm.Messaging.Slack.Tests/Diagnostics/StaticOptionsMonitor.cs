// -----------------------------------------------------------------------
// <copyright file="StaticOptionsMonitor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Diagnostics;

using System;
using Microsoft.Extensions.Options;

/// <summary>
/// Trivial <see cref="IOptionsMonitor{TOptions}"/> for unit tests that
/// just need a fixed value -- avoids dragging in the full
/// <c>Microsoft.Extensions.Options.ConfigurationExtensions</c>
/// composition. Shared across the Stage 7.3 diagnostics tests.
/// </summary>
internal sealed class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    where TOptions : class
{
    private readonly TOptions current;

    public StaticOptionsMonitor(TOptions current)
    {
        this.current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public TOptions CurrentValue => this.current;

    public TOptions Get(string? name) => this.current;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
