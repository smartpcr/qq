using System.Text.Json.Serialization;

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
/// via <see cref="JsonStringEnumConverter"/>. Numeric values stay stable for
/// in-process priority comparisons but the externalised JSON contract uses
/// names so cross-connector consumers and audit log readers remain robust to
/// future value re-ordering.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageSeverity
{
    Critical = 0,
    High = 1,
    Normal = 2,
    Low = 3,
}
