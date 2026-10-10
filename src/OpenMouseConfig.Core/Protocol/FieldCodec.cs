using System.Globalization;
using OpenMouseConfig.Core.Definitions;

namespace OpenMouseConfig.Core.Protocol;

/// <summary>Thrown when a payload can't be built or decoded (bad index, value out of range, ...).</summary>
public sealed class PayloadException(string message) : Exception(message);

/// <summary>
/// Converts a UI value into raw bytes at a given position of a report (and back again).
/// </summary>
/// <example>
/// DPI 1600 as a little-endian 16-bit value in units of 50 at byte 4:
/// <code>{ "name": "dpi", "index": 4, "size": 2, "divide": 50 }   →   report[4..6] = 20 00</code>
/// Motion sync stored in bit 3 of a flags byte:
/// <code>{ "name": "value", "index": 6, "mask": "0x08", "shift": 3 }</code>
/// </example>
public static class FieldCodec
{
    private const double Tolerance = 1e-6;

    /// <summary>Largest unsigned value that fits in <paramref name="size"/> bytes.</summary>
    public static long MaxRaw(int size) => size >= 8 ? long.MaxValue : (1L << (8 * size)) - 1;

    /// <summary>UI value → raw integer (map lookup, or linear transform).</summary>
    public static long ToRaw(FieldDefinition field, double value)
    {
        var map = field.GetMapEntries();
        if (map.Count > 0)
        {
            foreach (var entry in map)
            {
                if (Math.Abs(entry.Value - value) < Tolerance)
                {
                    return entry.Raw;
                }
            }

            var allowed = string.Join(", ", map.Select(e => e.Value.ToString(CultureInfo.InvariantCulture)));
            throw new PayloadException(
                $"Value {value.ToString(CultureInfo.InvariantCulture)} for '{field.Name}' is not in its map (allowed: {allowed}).");
        }

        return (long)Math.Round(value * field.Multiply / field.Divide + field.Add, MidpointRounding.AwayFromZero);
    }

    /// <summary>Raw integer → UI value. Returns null if a map is used and has no entry for <paramref name="raw"/>.</summary>
    public static double? FromRaw(FieldDefinition field, long raw)
    {
        var map = field.GetMapEntries();
        if (map.Count > 0)
        {
            foreach (var entry in map)
            {
                if (entry.Raw == raw)
                {
                    return entry.Value;
                }
            }

            return null;
        }

        return (raw - field.Add) * field.Divide / field.Multiply;
    }

    /// <summary>Writes <paramref name="value"/> into <paramref name="report"/> as described by <paramref name="field"/>.</summary>
    public static void Encode(Span<byte> report, FieldDefinition field, double value)
    {
        CheckBounds(report.Length, field);

        long raw = ToRaw(field, value);
        long fieldMax = MaxRaw(field.Size);
        long mask = field.Mask ?? fieldMax;
        long maxRaw = mask >> field.Shift;

        if (raw < 0 || raw > maxRaw || ((raw << field.Shift) & ~mask) != 0)
        {
            throw new PayloadException(
                $"'{field.Name}' = {value.ToString(CultureInfo.InvariantCulture)} becomes raw {raw}, which does not fit " +
                $"(allowed raw range 0..{maxRaw} at byte {field.Index}). Check multiply/divide/add or size.");
        }

        long current = ReadUnsigned(report, field.Index, field.Size, field.Endian);
        long updated = (current & ~mask) | ((raw << field.Shift) & mask);
        WriteUnsigned(report, field.Index, field.Size, field.Endian, updated);
    }

    /// <summary>Reads a value back out of a report (used for responses).</summary>
    public static double? Decode(ReadOnlySpan<byte> report, FieldDefinition field)
    {
        CheckBounds(report.Length, field);
        long mask = field.Mask ?? MaxRaw(field.Size);
        long raw = (ReadUnsigned(report, field.Index, field.Size, field.Endian) & mask) >> field.Shift;
        return FromRaw(field, raw);
    }

    private static void CheckBounds(int reportLength, FieldDefinition field)
    {
        if (field.Size is < 1 or > 4)
        {
            throw new PayloadException($"Field '{field.Name}': size must be 1-4 bytes.");
        }

        if (field.Index < 0 || field.Index + field.Size > reportLength)
        {
            throw new PayloadException(
                $"Field '{field.Name}' (index {field.Index}, size {field.Size}) lies outside the {reportLength}-byte report.");
        }
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> data, int index, int size, Endianness endian)
    {
        long value = 0;
        for (int i = 0; i < size; i++)
        {
            int b = endian == Endianness.Little ? data[index + i] : data[index + size - 1 - i];
            value |= (long)b << (8 * i);
        }

        return value;
    }

    private static void WriteUnsigned(Span<byte> data, int index, int size, Endianness endian, long value)
    {
        for (int i = 0; i < size; i++)
        {
            var b = (byte)((value >> (8 * i)) & 0xFF);
            if (endian == Endianness.Little)
            {
                data[index + i] = b;
            }
            else
            {
                data[index + size - 1 - i] = b;
            }
        }
    }
}
