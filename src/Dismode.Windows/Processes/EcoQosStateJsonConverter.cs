using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dismode.Windows.Processes;

internal static class EcoQosJournalCompatibility
{
    // Stary bool nie rozroznial Off i SystemManaged. Decyzja wlasciciela oczekuje zatwierdzenia.
    internal const EcoQosMode LegacyFalseMapsToSystemManaged = EcoQosMode.SystemManaged;
    internal const int CurrentSchemaVersion = 2;

    internal static EcoQosMode? ReadMode(JsonElement json)
    {
        if (json.TryGetProperty("SchemaVersion", out JsonElement version)
            && version.GetInt32() != CurrentSchemaVersion)
        {
            throw new JsonException("Nieobsługiwana wersja rekordu EcoQoS.");
        }

        if (json.TryGetProperty("Mode", out JsonElement mode))
        {
            if (mode.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return mode.GetString() switch
            {
                nameof(EcoQosMode.SystemManaged) => EcoQosMode.SystemManaged,
                nameof(EcoQosMode.On) => EcoQosMode.On,
                nameof(EcoQosMode.Off) => EcoQosMode.Off,
                _ => throw new JsonException("Nieznany tryb EcoQoS."),
            };
        }

        if (!json.TryGetProperty("ExecutionSpeedThrottled", out JsonElement legacy))
        {
            throw new JsonException("Brak trybu EcoQoS w rekordzie.");
        }

        return legacy.ValueKind == JsonValueKind.Null
            ? null
            : legacy.GetBoolean() ? EcoQosMode.On : LegacyFalseMapsToSystemManaged;
    }

    internal static void WriteMode(Utf8JsonWriter writer, EcoQosMode? mode)
    {
        writer.WriteNumber("SchemaVersion", CurrentSchemaVersion);
        writer.WriteString("Mode", mode?.ToString());
    }
}

internal sealed class ProcessEcoQosStateJsonConverter : JsonConverter<ProcessEcoQosState>
{
    public override ProcessEcoQosState Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return new(EcoQosJournalCompatibility.ReadMode(document.RootElement)
            ?? throw new JsonException("Brak trybu EcoQoS działającego procesu."));
    }

    public override void Write(
        Utf8JsonWriter writer,
        ProcessEcoQosState value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        EcoQosJournalCompatibility.WriteMode(writer, value.Mode);
        writer.WriteEndObject();
    }
}

internal sealed class RuntimeProcessEcoQosStateJsonConverter : JsonConverter<RuntimeProcessEcoQosState>
{
    public override RuntimeProcessEcoQosState Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement json = document.RootElement;
        bool isRunning = json.GetProperty("IsRunning").GetBoolean();
        EcoQosMode? mode = EcoQosJournalCompatibility.ReadMode(json);
        if (isRunning && mode is null)
        {
            throw new JsonException("Brak trybu EcoQoS działającego procesu.");
        }

        return new(isRunning, mode);
    }

    public override void Write(
        Utf8JsonWriter writer,
        RuntimeProcessEcoQosState value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("IsRunning", value.IsRunning);
        EcoQosJournalCompatibility.WriteMode(writer, value.Mode);
        writer.WriteEndObject();
    }
}
