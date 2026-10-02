using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Zeroshot.Native.Contracts;

/// <summary>Caller-owned wire data. Use NativeJson for validated serialization.</summary>
public abstract record NativeContract
{
    // Record-generated formatting would recursively expose authored inputs, instructions and scripts.
    public sealed override string ToString() => GetType().Name;

    /// <summary>
    /// The contract's own rules beyond strict typed decoding and nested pinned schemas. The serializer runs
    /// them on every decoded record, at any depth.
    /// </summary>
    internal virtual void Validate() { }

    /// <summary>Rules that need the received JSON itself. WireValidation runs them on a decoded root.</summary>
    internal virtual void CheckWire(JsonElement json) { }
}

internal static class ContractRules
{
    internal static void Attach(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !typeof(NativeContract).IsAssignableFrom(info.Type)) return;
        // A null non-nullable member fails before this hook, but a missing required one is only reported after it.
        var required = info.Properties.Where(p => p.IsRequired && !p.IsSetNullable && Absentable(p.PropertyType))
            .Select(p => (Get: p.Get!, Absent: p.PropertyType.IsValueType ? Activator.CreateInstance(p.PropertyType) : null)).ToArray();
        var declared = info.OnDeserialized;
        info.OnDeserialized = value =>
        {
            if (required.Any(member => Equals(member.Get(value), member.Absent))) return;
            declared?.Invoke(value);
            ((NativeContract)value).Validate();
        };
    }

    private static bool Absentable(Type type) =>
        !type.IsValueType || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
internal sealed class WireContractAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Separates an omitted field from a present value, including explicit null.</summary>
[JsonConverter(typeof(OptionalConverterFactory))]
public readonly struct Optional<T> : IEquatable<Optional<T>>
{
    private readonly T? value;
    public bool HasValue { get; }
    public T Value => HasValue ? value! : throw new InvalidOperationException("The field is omitted.");
    public Optional(T value) { this.value = value; HasValue = true; }
    public static Optional<T> Omitted => default;
    public static implicit operator Optional<T>(T value) => new(value);
    public bool Equals(Optional<T> other) => HasValue == other.HasValue && EqualityComparer<T>.Default.Equals(value, other.value);
    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(HasValue, value);
    public override string ToString() => HasValue ? "Present" : "Omitted";
}
