using System.Text.Json.Serialization;

namespace GameShift.Core.Domain.Identifiers;

public readonly record struct ActionId
{
    [JsonConstructor]
    public ActionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An action ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ActionId Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
