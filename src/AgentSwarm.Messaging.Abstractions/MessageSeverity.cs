using System.Text.Json.Serialization;
using AgentSwarm.Messaging.Abstractions.Json;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Priority severity for messenger traffic.
/// Ordered so ascending sort yields the priority order
/// (Critical first, Low last) used by the outbound queue's
/// priority dispatch and by alert routing.
/// </summary>
/// <remarks>
/// <para>
/// Integer values are load-bearing: see architecture.md Section 3.1
/// (<c>Critical &gt; High &gt; Normal &gt; Low</c>) and the matching
/// "MessageSeverity ordering" test scenario for stage 1.2.
/// </para>
/// <para>
/// Wire format: serialized as the member name string (e.g. <c>"High"</c>)
/// via <see cref="MessageSeverityJsonConverter"/>. The wire contract is
/// <em>names-only</em>: numeric tokens, numeric-string tokens (e.g. <c>"1"</c>),
/// case-mismatched names, and undefined values are rejected with
/// <see cref="System.Text.Json.JsonException"/>. Numeric values stay stable for
/// in-process priority comparisons but are not part of the externalised JSON
/// contract, so cross-connector consumers and audit log readers remain robust
/// to future value re-ordering.
/// </para>
/// </remarks>
[JsonConverter(typeof(MessageSeverityJsonConverter))]
public enum MessageSeverity
{
    Critical = 0,
    High = 1,
    Normal = 2,
    Low = 3,
}
