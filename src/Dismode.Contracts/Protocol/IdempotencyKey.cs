using System.Text.Json.Serialization;

namespace Dismode.Contracts.Protocol;

public readonly record struct IdempotencyKey
{
    [JsonConstructor]
    public IdempotencyKey(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static IdempotencyKey Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
