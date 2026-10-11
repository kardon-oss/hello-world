using OpenMouseConfig.Core.Definitions;

namespace OpenMouseConfig.Core.Protocol;

/// <summary>
/// Simple 8-bit checksums found in mouse configuration protocols. Applied to the fully
/// built, padded report so the checksum covers the injected values.
/// </summary>
public static class Checksum
{
    /// <summary>Computes the checksum and stores it at the configured index.</summary>
    public static void Apply(Span<byte> report, ChecksumDefinition definition)
    {
        if (definition.Type == ChecksumType.None)
        {
            return;
        }

        var (index, start, end) = ResolveRange(definition, report.Length);
        report[index] = Compute(report[start..(end + 1)], definition.Type, definition.Initial);
    }

    public static byte Compute(ReadOnlySpan<byte> data, ChecksumType type, int initial)
    {
        int acc = initial;
        foreach (var b in data)
        {
            acc = type switch
            {
                ChecksumType.Sum8 => acc + b,
                ChecksumType.Xor8 => acc ^ b,
                ChecksumType.SumComplement8 => acc - b,
                _ => acc,
            };
        }

        return (byte)(acc & 0xFF);
    }

    /// <summary>Turns negative / default indices into absolute positions and validates them.</summary>
    internal static (int Index, int Start, int End) ResolveRange(ChecksumDefinition definition, int reportLength)
    {
        int index = definition.Index < 0 ? reportLength + definition.Index : definition.Index;
        int start = definition.Start < 0 ? reportLength + definition.Start : definition.Start;
        int end = definition.End is { } e ? (e < 0 ? reportLength + e : e) : index - 1;

        if (index < 0 || index >= reportLength)
        {
            throw new PayloadException($"Checksum index {definition.Index} is outside the {reportLength}-byte report.");
        }

        if (start < 0 || end >= reportLength || start > end)
        {
            throw new PayloadException($"Checksum range {start}..{end} is invalid for a {reportLength}-byte report.");
        }

        if (index >= start && index <= end)
        {
            throw new PayloadException($"Checksum byte {index} lies inside its own range {start}..{end}.");
        }

        return (index, start, end);
    }
}
