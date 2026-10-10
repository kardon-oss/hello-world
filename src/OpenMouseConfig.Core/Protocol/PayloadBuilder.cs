using OpenMouseConfig.Core.Definitions;

namespace OpenMouseConfig.Core.Protocol;

/// <summary>
/// Turns a <see cref="CommandDefinition"/> from devices.json plus the current UI values into the
/// exact bytes handed to Windows.
/// </summary>
/// <remarks>
/// Steps:
/// <list type="number">
///   <item>Parse the hex template (byte 0 = report ID).</item>
///   <item>Zero-pad it to the report length the device declares (HIDP_CAPS), because
///         HidD_SetFeature/WriteFile require a buffer of exactly that size.</item>
///   <item>Inject every field (value → raw → bytes at index).</item>
///   <item>Compute the checksum, if the transport defines one.</item>
/// </list>
/// </remarks>
public static class PayloadBuilder
{
    /// <param name="command">Command template.</param>
    /// <param name="checksum">Checksum rule from the transport, or null.</param>
    /// <param name="reportLength">
    /// Full report length including the report ID. 0 = unknown (offline dry-run without
    /// transport.reportLength) → the template length is used.
    /// </param>
    /// <param name="variables">Values the fields can reference (see <see cref="VariableNames"/>).</param>
    public static byte[] Build(
        CommandDefinition command,
        ChecksumDefinition? checksum,
        int reportLength,
        IReadOnlyDictionary<string, double> variables)
    {
        var template = command.GetTemplate();
        if (template.Length == 0)
        {
            throw new PayloadException("Payload is empty; it must at least contain the report ID.");
        }

        int length = reportLength > 0 ? reportLength : template.Length;
        if (template.Length > length)
        {
            throw new PayloadException(
                $"Payload is {template.Length} bytes but the device's report is only {length} bytes " +
                "(remember: byte 0 is the report ID).");
        }

        var report = new byte[length];
        template.CopyTo(report, 0);

        foreach (var field in command.Fields)
        {
            if (!variables.TryGetValue(field.Name, out var value))
            {
                throw new PayloadException(
                    $"No value named '{field.Name}' is available to this command. " +
                    $"Available: {string.Join(", ", variables.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");
            }

            FieldCodec.Encode(report, field, value);
        }

        if (checksum is { Type: not ChecksumType.None })
        {
            if (reportLength <= 0)
            {
                throw new PayloadException(
                    "Can't place the checksum: the report length is unknown. Set transport.reportLength (needed for offline dry-run).");
            }

            Checksum.Apply(report, checksum);
        }

        return report;
    }

    /// <summary>Decodes all fields of a response. Fields whose raw value isn't in their map are skipped.</summary>
    public static Dictionary<string, double> Decode(ResponseDefinition response, ReadOnlySpan<byte> report)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in response.Fields)
        {
            if (FieldCodec.Decode(report, field) is { } value)
            {
                values[field.Name] = value;
            }
        }

        return values;
    }
}
