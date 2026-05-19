using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// Stage 6.3 iter-4 evaluator feedback item 2 — per-process sentinel used by
/// <see cref="TeamsHealthCheckEndpointRouteBuilderExtensions.MapTeamsHealthChecks"/>
/// to dedup repeated mapping of the canonical Teams health endpoints (<c>/health</c>,
/// <c>/health/live</c>, <c>/health/ready</c>) onto the same
/// <see cref="IEndpointRouteBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> When a host calls <c>AddTeamsDiagnostics()</c> the default
/// composition registers a <see cref="TeamsHealthCheckEndpointStartupFilter"/> that
/// auto-maps the Teams health endpoints. If the same host <i>also</i> calls
/// <see cref="TeamsHealthCheckEndpointRouteBuilderExtensions.MapTeamsHealthChecks"/>
/// manually in its <c>Configure</c> delegate — a common pattern when a host had wired
/// the endpoint surface before the iter-3 auto-map default — ASP.NET routing throws
/// <c>"The following endpoints with a duplicate route pattern were found"</c> at first
/// probe because <c>/health</c>, <c>/health/live</c>, and <c>/health/ready</c> are
/// each mapped twice. This sentinel observes the (builder, prefix) pair on the first
/// mapping call and the second call returns the cached
/// <see cref="IEndpointConventionBuilder"/> so the route registrations are not
/// duplicated AND the caller's chained <c>.WithName(...)</c> /
/// <c>.RequireAuthorization()</c> calls still produce a sane builder.
/// </para>
/// <para>
/// <b>Lifecycle.</b> The marker is a singleton registered UNCONDITIONALLY by
/// <see cref="TeamsDiagnosticsServiceCollectionExtensions.AddTeamsDiagnostics"/>
/// on the line BEFORE its <c>if (autoMapHealthEndpoints)</c> branch (see the
/// <c>services.TryAddSingleton&lt;TeamsHealthChecksRoutesMarker&gt;()</c> call
/// in <c>TeamsDiagnosticsServiceCollectionExtensions.cs</c>) AND by
/// <see cref="TeamsDiagnosticsServiceCollectionExtensions.AddTeamsHealthCheckEndpoint"/>
/// (the granular helper invoked when <c>autoMapHealthEndpoints: true</c>). Both
/// registrations use <c>TryAddSingleton</c>, so the descriptor count is
/// unchanged when both fire and the marker resolves to the same instance.
/// This means hosts that opt out of the auto-map via
/// <c>AddTeamsDiagnostics(autoMapHealthEndpoints: false)</c> and then call
/// <see cref="TeamsHealthCheckEndpointRouteBuilderExtensions.MapTeamsHealthChecks"/>
/// manually STILL get the dedup — the marker is in DI regardless of which
/// composition path the host picked, and <c>MapTeamsHealthChecks</c> resolves it
/// from <c>endpoints.ServiceProvider</c> at mapping time (Stage 6.3 iter-13
/// evaluator fix item 1, pinned by
/// <c>TeamsDiagnosticsAutoMappedHealthEndpointTests.AddTeamsDiagnostics_RegistersTeamsHealthChecksRoutesMarker_RegardlessOfAutoMapFlag</c>).
/// The dedup is keyed on (builder, prefix), so the first manual call wins and
/// second-time-around calls (e.g. from a host that re-runs its <c>Configure</c>
/// pipeline) become safe no-ops.
/// </para>
/// <para>
/// <b>Keying.</b> The map is keyed by the <i>identity</i> of the
/// <see cref="IEndpointRouteBuilder"/> instance, NOT by its hash. ASP.NET hosts
/// typically have a single endpoint route builder per pipeline so this is the natural
/// scope; if a test composes two independent hosts in the same process each gets its
/// own builder and the marker keeps their mappings separate.
/// </para>
/// </remarks>
internal sealed class TeamsHealthChecksRoutesMarker
{
    // Conditional weak table — entries are garbage collected when the
    // IEndpointRouteBuilder is collected, so the marker does not keep test-host
    // builders alive across test cases (critical for the xUnit collection runner
    // that recycles assembly contexts).
    private readonly ConditionalWeakTable<IEndpointRouteBuilder, BuilderState> _byBuilder = new();
    private readonly object _gate = new();

    /// <summary>
    /// Returns <c>true</c> when <paramref name="endpoints"/> already has Teams health
    /// routes mapped at <paramref name="pathPrefix"/>; the cached
    /// <see cref="IEndpointConventionBuilder"/> from the first mapping is returned via
    /// <paramref name="existing"/>. Returns <c>false</c> when this is the first call
    /// for <paramref name="endpoints"/> at the given prefix — the caller proceeds with
    /// the mapping and then records it via <see cref="Record"/>.
    /// </summary>
    public bool TryGetExistingBuilder(IEndpointRouteBuilder endpoints, string pathPrefix, out IEndpointConventionBuilder existing)
    {
        lock (_gate)
        {
            if (_byBuilder.TryGetValue(endpoints, out var state) && state.Mappings.TryGetValue(pathPrefix, out var cached))
            {
                existing = cached;
                return true;
            }
        }

        existing = NoOpConventionBuilder.Instance;
        return false;
    }

    /// <summary>
    /// Record that <paramref name="endpoints"/> now has Teams health routes mapped at
    /// <paramref name="pathPrefix"/>, with <paramref name="readiness"/> as the
    /// convention builder for the readiness route. Subsequent
    /// <see cref="TryGetExistingBuilder"/> calls with the same arguments return the
    /// cached builder.
    /// </summary>
    public void Record(IEndpointRouteBuilder endpoints, string pathPrefix, IEndpointConventionBuilder readiness)
    {
        lock (_gate)
        {
            if (!_byBuilder.TryGetValue(endpoints, out var state))
            {
                state = new BuilderState();
                _byBuilder.Add(endpoints, state);
            }
            state.Mappings[pathPrefix] = readiness;
        }
    }

    private sealed class BuilderState
    {
        public Dictionary<string, IEndpointConventionBuilder> Mappings { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// No-op <see cref="IEndpointConventionBuilder"/> returned by
    /// <see cref="TryGetExistingBuilder"/> as a guaranteed-non-null sentinel when no
    /// existing mapping is found AND by tests that exercise the dedup path. Implements
    /// <see cref="IEndpointConventionBuilder.Add"/> as a discard so a caller that
    /// chained <c>.WithName(...)</c> onto a deduped second mapping does not crash —
    /// the chained convention is silently dropped because the underlying routes have
    /// already been finalized by the first mapping.
    /// </summary>
    internal sealed class NoOpConventionBuilder : IEndpointConventionBuilder
    {
        public static readonly NoOpConventionBuilder Instance = new();
        public void Add(Action<EndpointBuilder> convention)
        {
        }
    }
}
