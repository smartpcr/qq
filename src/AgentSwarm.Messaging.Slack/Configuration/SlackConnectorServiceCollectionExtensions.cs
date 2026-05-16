using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSwarm.Messaging.Slack.Configuration;

/// <summary>
/// Service collection extensions that bind the Slack connector options
/// from <see cref="IConfiguration"/>.
/// </summary>
public static class SlackConnectorServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="SlackConnectorOptions"/> from the
    /// <see cref="SlackConnectorOptions.SectionName"/> configuration
    /// section using the standard
    /// <see cref="OptionsConfigurationServiceCollectionExtensions.Configure{TOptions}(IServiceCollection, IConfiguration)"/>
    /// helper. Idempotent: calling more than once with the same
    /// configuration is harmless (the last call wins per the options
    /// pattern).
    /// </summary>
    /// <param name="services">The DI container to register the options into.</param>
    /// <param name="configuration">
    /// The application configuration root. The
    /// <see cref="SlackConnectorOptions.SectionName"/> section is read
    /// from this configuration; the section may be absent, in which
    /// case the default values on the POCO take effect.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or
    /// <paramref name="configuration"/> is <c>null</c>.
    /// </exception>
    public static IServiceCollection AddSlackConnectorOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SlackConnectorOptions>(
            configuration.GetSection(SlackConnectorOptions.SectionName));
        return services;
    }
}
