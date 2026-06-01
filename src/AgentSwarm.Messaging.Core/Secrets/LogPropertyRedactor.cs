// -----------------------------------------------------------------------
// <copyright file="LogPropertyRedactor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

/// <summary>
/// Built-in interceptor that reads
/// <see cref="LogPropertyIgnoreAttribute"/> and produces a redacted
/// <c>ToString</c>-style rendering of any instance: properties tagged
/// with the attribute are replaced with
/// <see cref="SecretScrubber.Placeholder"/> while non-secret properties
/// are emitted as-is.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>:
/// the marker attribute alone is insufficient because a careless
/// <c>logger.LogInformation("entry={Entry}", obj)</c> calls
/// <see cref="object.ToString"/> on the captured argument and most
/// destructurers do not understand a custom marker. This redactor makes
/// <see cref="LogPropertyIgnoreAttribute"/> <em>enforceable</em>:
/// secret-holding types delegate their <c>ToString</c> override to
/// <see cref="RedactToString"/>, which guarantees the rendered string
/// substitutes the scrubber placeholder for every annotated member --
/// regardless of how the holder type evolves.
/// </para>
/// <para>
/// Property metadata is cached per concrete <see cref="Type"/> so the
/// reflection cost is paid once per type lifetime.
/// </para>
/// </remarks>
public static class LogPropertyRedactor
{
    private static readonly ConcurrentDictionary<Type, PropertyDescriptor[]> Descriptors = new();

    /// <summary>
    /// Renders <paramref name="instance"/> in the conventional
    /// "<c>TypeName(Prop1=value1, Prop2=***)</c>" shape, replacing every
    /// member tagged with <see cref="LogPropertyIgnoreAttribute"/> with
    /// <see cref="SecretScrubber.Placeholder"/>. Members with a
    /// <see langword="null"/> or empty string value receive
    /// <see cref="SecretScrubber.EmptyPlaceholder"/> when annotated.
    /// </summary>
    /// <param name="instance">
    /// The instance to render. <see langword="null"/> returns the
    /// literal <c>(null)</c> placeholder so an inadvertent
    /// <c>logger.LogInformation("entry={Entry}", maybeNull)</c> still
    /// produces a deterministic, non-throwing string.
    /// </param>
    /// <returns>The redacted rendering.</returns>
    public static string RedactToString(object? instance)
    {
        if (instance is null)
        {
            return "(null)";
        }

        Type type = instance.GetType();
        PropertyDescriptor[] descriptors = Descriptors.GetOrAdd(type, BuildDescriptors);

        StringBuilder sb = new();
        sb.Append(type.Name).Append('(');

        for (int i = 0; i < descriptors.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            PropertyDescriptor descriptor = descriptors[i];
            sb.Append(descriptor.Name).Append('=');

            if (descriptor.IsSecret)
            {
                // Even if the property throws on read, we MUST NOT leak
                // the exception text -- it could embed the very secret
                // that prompted the throw. Render the placeholder
                // unconditionally when the member is secret-tagged.
                object? raw;
                try
                {
                    raw = descriptor.Getter(instance);
                }
                catch
                {
                    sb.Append(SecretScrubber.Placeholder);
                    continue;
                }

                sb.Append(raw is null || (raw is string s && s.Length == 0)
                    ? SecretScrubber.EmptyPlaceholder
                    : SecretScrubber.Placeholder);
                continue;
            }

            object? value;
            try
            {
                value = descriptor.Getter(instance);
            }
            catch (Exception ex)
            {
                sb.Append("<threw ").Append(ex.GetType().Name).Append('>');
                continue;
            }

            sb.Append(RenderNonSecret(value));
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string RenderNonSecret(object? value)
    {
        return value switch
        {
            null => "(null)",
            string s => s,
            IFormattable f => f.ToString(format: null, formatProvider: CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "(null)",
        };
    }

    private static PropertyDescriptor[] BuildDescriptors(Type type)
    {
        PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        return props
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.GetGetMethod() is not null)
            .Select(p => new PropertyDescriptor(
                Name: p.Name,
                IsSecret: p.GetCustomAttribute<LogPropertyIgnoreAttribute>() is not null,
                Getter: BuildGetter(p)))
            .ToArray();
    }

    private static Func<object, object?> BuildGetter(PropertyInfo property)
    {
        MethodInfo getter = property.GetGetMethod() ?? throw new InvalidOperationException(
            $"Property {property.DeclaringType?.FullName ?? "<unknown>"}.{property.Name} has no public getter.");

        return instance => getter.Invoke(instance, parameters: null);
    }

    private sealed record PropertyDescriptor(string Name, bool IsSecret, Func<object, object?> Getter);
}
