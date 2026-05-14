// NOTE (Stage 2.1 cleanup): the OutboundMessage record together with the
// OutboundSourceType / OutboundMessageStatus enums were relocated from
// AgentSwarm.Messaging.Core to AgentSwarm.Messaging.Abstractions during
// Stage 1.4 so that IOutboundQueue (which lives in Abstractions per the
// brief) can reference them without forcing a forbidden
// Abstractions -> Core project reference. The canonical definitions now
// live in src/AgentSwarm.Messaging.Abstractions/OutboundMessage.cs. The
// duplicate definitions that previously lived here caused CS0104
// (ambiguous reference) in OutboundContractTests.cs against the
// AgentSwarm.Messaging.Tests project which has using-directives for both
// Abstractions and Core. This file is retained as a stub so the source
// tree stays stable; it intentionally exports no types.
namespace AgentSwarm.Messaging.Core;
