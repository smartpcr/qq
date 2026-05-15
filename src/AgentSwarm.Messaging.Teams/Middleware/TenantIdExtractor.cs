using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// Shared helper that extracts the Teams tenant ID from a buffered HTTP request body
/// containing a Bot Framework <c>Activity</c> JSON payload. Used by both
/// <see cref="TenantValidationMiddleware"/> (HTTP 403 enforcement) and
/// <see cref="RateLimitMiddleware"/> (per-tenant 429 keying) to keep the two pipelines in
/// lock-step on what counts as the canonical tenant identifier.
/// </summary>
/// <remarks>
/// <para>
/// The extraction order matches the precedence that Microsoft's Bot Framework SDK observes
/// for Teams payloads:
/// <list type="number">
///   <item><c>$.channelData.tenant.id</c> — the Teams-specific channel-data extension; this
///   is the canonical tenant identifier on inbound activities and is populated by both
///   personal-chat and channel-scoped messages.</item>
///   <item><c>$.conversation.tenantId</c> — the Bot Framework <c>Conversation.TenantId</c>
///   fallback present on some activity shapes (Notably, activities synthesized from the
///   Teams installation events). Used only when the channel-data path is absent.</item>
/// </list>
/// </para>
/// <para>
/// The caller is responsible for enabling buffering on the request body
/// (<see cref="HttpRequestRewindExtensions.EnableBuffering(HttpRequest)"/>) before invoking
/// this helper, and for rewinding <see cref="HttpRequest.Body"/> back to position 0 after the
/// call so downstream middleware (most importantly <c>CloudAdapter</c>) can re-read the
/// payload. This helper does NOT mutate the request body position itself, so callers can
/// reset it deterministically once the extraction completes (success or failure).
/// </para>
/// <para>
/// Returns <c>null</c> when the body is empty, missing, malformed JSON, or contains no
/// tenant identifier on either supported path. The helper never throws on malformed JSON —
/// callers translate <c>null</c> into the appropriate HTTP rejection (typically 403 from
/// <see cref="TenantValidationMiddleware"/>).
/// </para>
/// </remarks>
internal static class TenantIdExtractor
{
    /// <summary>
    /// Per-request cache slot key used by <see cref="GetOrExtractFromBodyAsync"/>. An
    /// <see cref="object"/> instance (not a string) so the entry cannot collide with any
    /// caller-defined key in <see cref="HttpContext.Items"/>.
    /// </summary>
    private static readonly object CacheKey = new();

    /// <summary>
    /// Sentinel cached for the "extraction completed but yielded no tenant" case so that
    /// cache hits can distinguish a legitimate <c>null</c> result (already attempted) from
    /// a missing cache entry (never attempted). Stored under <see cref="CacheKey"/> in
    /// <see cref="HttpContext.Items"/>.
    /// </summary>
    private static readonly object NoTenantSentinel = new();

    /// <summary>
    /// Read the buffered HTTP request body and return the Teams tenant ID, or <c>null</c>
    /// when the body is empty, malformed, or carries no tenant on either supported path.
    /// This overload always re-parses the body — callers that want per-request caching across
    /// multiple ASP.NET Core middleware stages should use <see cref="GetOrExtractFromBodyAsync"/>
    /// instead.
    /// </summary>
    /// <param name="context">The current HTTP context. <c>Request.Body</c> must be seekable
    /// (caller has already enabled buffering).</param>
    /// <param name="cancellationToken">Cancellation token co-operating with the request
    /// lifetime.</param>
    /// <returns>The extracted tenant ID, or <c>null</c>.</returns>
    public static async Task<string?> TryExtractFromBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        var body = context.Request.Body;
        if (!body.CanSeek)
        {
            // Defensive: callers should EnableBuffering() first. Without seek support we
            // cannot safely re-read the payload, and the only correct behavior is to abort
            // the lookup so downstream middleware sees the original (un-consumed) stream.
            return null;
        }

        // ContentLength == 0 is an explicit empty body. Null ContentLength (chunked
        // transfer) is allowed — fall through to ParseAsync, which will return early on an
        // empty payload.
        if (context.Request.ContentLength == 0)
        {
            return null;
        }

        try
        {
            body.Position = 0;
            using var doc = await JsonDocument.ParseAsync(
                body,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Path 1: channelData.tenant.id (Teams-specific).
            if (root.TryGetProperty("channelData", out var channelData) &&
                channelData.ValueKind == JsonValueKind.Object &&
                channelData.TryGetProperty("tenant", out var tenant) &&
                tenant.ValueKind == JsonValueKind.Object &&
                tenant.TryGetProperty("id", out var tenantIdProp) &&
                tenantIdProp.ValueKind == JsonValueKind.String)
            {
                var fromChannelData = tenantIdProp.GetString();
                if (!string.IsNullOrWhiteSpace(fromChannelData))
                {
                    return fromChannelData;
                }
            }

            // Path 2: conversation.tenantId fallback.
            if (root.TryGetProperty("conversation", out var conversation) &&
                conversation.ValueKind == JsonValueKind.Object &&
                conversation.TryGetProperty("tenantId", out var convTenant) &&
                convTenant.ValueKind == JsonValueKind.String)
            {
                var fromConversation = convTenant.GetString();
                if (!string.IsNullOrWhiteSpace(fromConversation))
                {
                    return fromConversation;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — caller treats null as "no tenant identified" and rejects
            // appropriately.
            return null;
        }

        return null;
    }

    /// <summary>
    /// Per-request cached variant of <see cref="TryExtractFromBodyAsync"/>. The first call in
    /// the ASP.NET Core HTTP pipeline parses the body and stashes the result (including a
    /// negative result) under a private key in <see cref="HttpContext.Items"/>; subsequent
    /// callers in the same request hit the cache and avoid re-parsing the body.
    /// </summary>
    /// <param name="context">The current HTTP context. <c>Request.Body</c> must be seekable
    /// when the cache misses (caller has already enabled buffering).</param>
    /// <param name="cancellationToken">Cancellation token co-operating with the request
    /// lifetime.</param>
    /// <returns>The extracted tenant ID, or <c>null</c> when no tenant can be determined.</returns>
    /// <remarks>
    /// Used by both <see cref="TenantValidationMiddleware"/> (the first ASP.NET Core HTTP
    /// middleware in the pipeline) and <see cref="RateLimitMiddleware"/> (the second stage).
    /// Without this cache the second stage would re-buffer and re-parse the same JSON
    /// payload — a measurable cost on the hot path because every inbound activity flows
    /// through both stages. The cache key is a private <see cref="object"/> instance, not
    /// a string literal, to guarantee no accidental collisions with caller-defined entries
    /// in <see cref="HttpContext.Items"/>.
    /// </remarks>
    public static async Task<string?> GetOrExtractFromBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (context.Items.TryGetValue(CacheKey, out var cached))
        {
            // Cache hit — we have already attempted extraction once on this request.
            // ReferenceEquals against the sentinel disambiguates "extracted to null" from
            // "missing entry" so even cached null results short-circuit re-parsing.
            return ReferenceEquals(cached, NoTenantSentinel) ? null : (string?)cached;
        }

        var extracted = await TryExtractFromBodyAsync(context, cancellationToken).ConfigureAwait(false);
        context.Items[CacheKey] = extracted is null ? NoTenantSentinel : (object)extracted;
        return extracted;
    }
}
