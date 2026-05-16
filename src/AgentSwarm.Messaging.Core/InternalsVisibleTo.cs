using System.Runtime.CompilerServices;

// Exposes Stage 3.3 internal caching / option-resolution helpers to the
// Slack test assembly. Production consumers must only see these through
// the public ISecretProvider DI surface registered by
// SecretProviderServiceCollectionExtensions.AddSecretProvider.
[assembly: InternalsVisibleTo("AgentSwarm.Messaging.Slack.Tests")]
