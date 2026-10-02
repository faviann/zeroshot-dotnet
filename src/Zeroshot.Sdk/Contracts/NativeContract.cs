using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>Caller-owned wire data. Use NativeJson for validated serialization.</summary>
public abstract record NativeContract
{
    // Record-generated formatting would recursively expose authored inputs, instructions and scripts.
    public sealed override string ToString() => GetType().Name;

    /// <summary>
    /// The contract's own wire rules beyond strict typed decoding and nested pinned schemas. WireValidation
    /// runs them on a decoded root with its raw JSON.
    /// </summary>
    internal virtual void Validate(JsonElement json) { }
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
