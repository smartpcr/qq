using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSwarm.Messaging.Abstractions.Json;

/// <summary>
/// JSON converter for enums that enforces a strict <em>names-only</em> wire
/// contract: on write it emits the canonical member name; on read it accepts
/// <strong>only</strong> a JSON string token whose value exactly matches one of
/// the declared member names (case-sensitive). Numeric tokens, numeric-string
/// tokens (e.g. <c>"1"</c>), case-mismatched names, comma-combined flag
/// strings, and undefined values all throw <see cref="JsonException"/>.
/// </summary>
/// <remarks>
/// The shared messenger contract pins enum representation to member names so
/// cross-connector consumers (Discord, Telegram, Slack, Teams, EF persistence,
/// audit log readers) remain robust to underlying integer re-ordering. The
/// built-in <see cref="JsonStringEnumConverter"/> writes names but accepts
/// integers on read, leaving the wire contract permissive; this converter
/// closes that gap.
/// </remarks>
/// <typeparam name="TEnum">The enum type to convert.</typeparam>
public class StrictNamedEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <inheritdoc />
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected a JSON string for {typeof(TEnum).Name}; got {reader.TokenType}. The wire contract is names-only.");
        }

        var name = reader.GetString();
        if (string.IsNullOrEmpty(name))
        {
            throw new JsonException(
                $"{typeof(TEnum).Name} value must be a non-empty member name string.");
        }

        if (!Enum.TryParse<TEnum>(name, ignoreCase: false, out var value)
            || !Enum.IsDefined(value)
            || !string.Equals(Enum.GetName(value), name, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"'{name}' is not a defined member name of {typeof(TEnum).Name}. The wire contract is names-only (case-sensitive, no numeric strings, no flag combinations).");
        }

        return value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        // Use Enum.GetName, not value.ToString(): the latter happily emits the
        // numeric string for undefined values like (MessageSeverity)99, which
        // would violate the names-only wire contract and create a write/read
        // asymmetry (Read would then reject what Write just produced).
        var name = Enum.GetName(value);
        if (name is null)
        {
            throw new JsonException(
                $"Cannot serialize undefined {typeof(TEnum).Name} value '{value}'. The wire contract is names-only.");
        }

        writer.WriteStringValue(name);
    }
}

/// <summary>Strict names-only converter for <see cref="MessageSeverity"/>.</summary>
public sealed class MessageSeverityJsonConverter : StrictNamedEnumConverter<MessageSeverity>
{
}

/// <summary>Strict names-only converter for <see cref="ChannelPurpose"/>.</summary>
public sealed class ChannelPurposeJsonConverter : StrictNamedEnumConverter<ChannelPurpose>
{
}

/// <summary>Strict names-only converter for <see cref="MessengerEventType"/>.</summary>
public sealed class MessengerEventTypeJsonConverter : StrictNamedEnumConverter<MessengerEventType>
{
}

/// <summary>Strict names-only converter for <see cref="OutboundMessageStatus"/>.</summary>
public sealed class OutboundMessageStatusJsonConverter : StrictNamedEnumConverter<OutboundMessageStatus>
{
}

/// <summary>Strict names-only converter for <see cref="OutboundMessageSource"/>.</summary>
public sealed class OutboundMessageSourceJsonConverter : StrictNamedEnumConverter<OutboundMessageSource>
{
}

/// <summary>Strict names-only converter for <see cref="PendingQuestionStatus"/>.</summary>
public sealed class PendingQuestionStatusJsonConverter : StrictNamedEnumConverter<PendingQuestionStatus>
{
}
