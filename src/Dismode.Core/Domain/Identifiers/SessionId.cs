using System.Text.Json.Serialization;

namespace Dismode.Core.Domain.Identifiers;

public readonly record struct SessionId
{
    [JsonConstructor]
    public SessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A session ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static SessionId Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
