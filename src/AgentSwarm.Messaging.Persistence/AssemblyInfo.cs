using System.Runtime.CompilerServices;

// Stage 2.3 atomic-claim coverage: PersistentOutboundQueueTests calls
// the internal TryClaimAsync seam directly to deterministically prove
// the conditional-UPDATE CAS rejects a stale-snapshot claim attempt.
[assembly: InternalsVisibleTo("AgentSwarm.Messaging.Tests")]
