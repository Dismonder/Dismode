using System.Text.Json.Serialization;

namespace GameShift.Core.Domain.Identifiers;

public readonly record struct GameProfileId
{
    [JsonConstructor]
    public GameProfileId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "A game profile ID cannot be empty.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static GameProfileId Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
