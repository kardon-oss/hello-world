using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMouseConfig.Core.Protocol;

namespace OpenMouseConfig.Core.Definitions;

/// <summary>System.Text.Json settings tuned for hand-written definition files.</summary>
public static class DefinitionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,

            // People annotate their captures; let them.
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,

            // "multiply": "0.5" is as valid as "multiply": 0.5.
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            WriteIndented = true,
        };

        // Integers may be written as numbers, "1234" or "0x04D2".
        options.Converters.Add(new FlexibleIntConverter());
        options.Converters.Add(new FlexibleLongConverter()); // masks/map values up to 0xFFFFFFFF

        // Enums as strings ("feature", "output", "sumComplement8"). Reading is case-insensitive;
        // numeric enum values are rejected because they'd silently map to nonsense.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

/// <summary>Reads an <see cref="int"/> from a JSON number or from a decimal/hex string ("0x1A").</summary>
public sealed class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var number))
                {
                    return number;
                }

                throw new JsonException("Number is not a whole 32-bit integer.");

            case JsonTokenType.String:
                var text = reader.GetString();
                if (HexFormat.TryParseInteger(text, out var parsed) && parsed is >= int.MinValue and <= int.MaxValue)
                {
                    return (int)parsed;
                }

                throw new JsonException($"\"{text}\" is not an integer. Use a number (26000) or hex string (\"0x6590\").");

            default:
                throw new JsonException($"Expected an integer or hex string but found {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>64-bit variant of <see cref="FlexibleIntConverter"/>, for masks and map values that may use bit 31.</summary>
public sealed class FlexibleLongConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var number))
                {
                    return number;
                }

                throw new JsonException("Number is not a whole 64-bit integer.");

            case JsonTokenType.String:
                var text = reader.GetString();
                if (HexFormat.TryParseInteger(text, out var parsed))
                {
                    return parsed;
                }

                throw new JsonException($"\"{text}\" is not an integer. Use a number (255) or hex string (\"0xFF\").");

            default:
                throw new JsonException($"Expected an integer or hex string but found {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
