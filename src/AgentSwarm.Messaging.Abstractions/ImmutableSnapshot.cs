using System.Collections.ObjectModel;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Internal helpers for snapshotting caller-supplied collections into truly
/// immutable wrappers. The returned values cannot be downcast back to their
/// underlying mutable types (e.g. <c>T[]</c>, <see cref="List{T}"/>,
/// <see cref="Dictionary{TKey, TValue}"/>) and mutating members on the
/// <see cref="IList{T}"/>/<see cref="IDictionary{TKey, TValue}"/> views throw
/// <see cref="NotSupportedException"/>. Used by shared DTOs to honour the
/// "shared contract cannot be mutated after construction" promise.
/// </summary>
internal static class ImmutableSnapshot
{
    public static IReadOnlyDictionary<string, string>? FromStringMap(
        IReadOnlyDictionary<string, string>? value,
        string paramName)
    {
        if (value is null)
        {
            return null;
        }

        var copy = new Dictionary<string, string>(value.Count, StringComparer.Ordinal);
        foreach (var kv in value)
        {
            if (kv.Value is null)
            {
                // Symmetry with GuildBinding.ToImmutableRestrictions: surface
                // malformed config / JSON loudly rather than silently coercing.
                throw new ArgumentException(
                    $"{paramName}['{kv.Key}'] must not be null.",
                    paramName);
            }

            copy[kv.Key] = kv.Value;
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    public static IReadOnlyDictionary<string, string> FromRequiredStringMap(
        IReadOnlyDictionary<string, string>? value,
        string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }

        var copy = new Dictionary<string, string>(value.Count, StringComparer.Ordinal);
        foreach (var kv in value)
        {
            if (kv.Value is null)
            {
                throw new ArgumentException(
                    $"{paramName}['{kv.Key}'] must not be null.",
                    paramName);
            }

            copy[kv.Key] = kv.Value;
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}
