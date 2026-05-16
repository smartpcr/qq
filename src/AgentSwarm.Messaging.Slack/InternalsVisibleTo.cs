using System.Runtime.CompilerServices;

// Exposes Stage 1.3 internal queue / retry contracts and Stage 3.1 / 4.1
// envelope types to the Slack test assembly. Production consumers must
// only see these types through DI-registered service interfaces.
[assembly: InternalsVisibleTo("AgentSwarm.Messaging.Slack.Tests")]
