// -----------------------------------------------------------------------
// <copyright file="LogPropertyIgnoreAttribute.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;

/// <summary>
/// Marks a member (property, field, or parameter) as holding a secret
/// value that MUST NOT be reflected into log output, audit entries, or
/// any other persisted diagnostic surface.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (architecture.md §7.3): "Bot tokens and signing secrets are stored
/// in a secret provider… Secrets are never logged or included in audit
/// entries."
/// </para>
/// <para>
/// The attribute is read by the built-in
/// <see cref="LogPropertyRedactor"/> interceptor: every secret-holding
/// type in this codebase overrides <see cref="object.ToString"/> to
/// delegate to <see cref="LogPropertyRedactor.RedactToString"/>, which
/// reflects over the instance and substitutes
/// <see cref="SecretScrubber.Placeholder"/> for every annotated
/// member. That means a careless
/// <c>logger.LogInformation("entry={Entry}", cacheEntry)</c> -- which
/// calls <c>ToString()</c> on the captured argument before formatting
/// it into the log message -- emits the scrubbed placeholder instead
/// of the raw secret, and the guarantee survives the addition of new
/// properties without requiring every author to remember to update a
/// hand-written <c>ToString</c>.
/// </para>
/// <para>
/// Third-party structured loggers that walk an object graph
/// (Serilog destructurers, custom <c>ILogEnricher</c> implementations,
/// etc.) can also honour the attribute by reading it directly via
/// reflection; <see cref="LogPropertyRedactor"/> is provided as a
/// ready-to-use implementation so the marker is enforceable inside
/// this codebase without depending on any external logging library
/// inspecting it.
/// </para>
/// <para>
/// The attribute is sealed and not inheritable: secrets are an
/// all-or-nothing concern and silent inheritance through a base class
/// would make a missing annotation on the most-derived member
/// invisible at the call site.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = false)]
public sealed class LogPropertyIgnoreAttribute : Attribute
{
}
