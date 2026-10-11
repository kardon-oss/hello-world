using System.Globalization;
using System.Text;

namespace OpenMouseConfig.Core.Protocol;

/// <summary>
/// Converts between the hex strings people paste into devices.json (straight out of
/// Wireshark, a WebHID console hook, or a vendor's JavaScript) and byte arrays.
/// </summary>
/// <remarks>
/// Accepted input styles, all equivalent:
/// <code>
///   "05 0A 00 FF"      space separated (Wireshark "Copy as Hex Stream" with spaces)
///   "050A00FF"         one contiguous hex stream
///   "0x05, 0x0A, 0xFF" C/JS array style
///   "05:0a:00:ff"      colon/dash separated, any case
/// </code>
/// </remarks>
public static class HexFormat
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', ';', ':', '-', '|', '[', ']'];

    /// <summary>Parses a hex byte string. Wildcards are not allowed here (see <see cref="BytePattern"/>).</summary>
    /// <exception cref="FormatException">The text contains something that isn't a hex byte.</exception>
    public static byte[] Parse(string? text)
    {
        var tokens = Tokenize(text, allowWildcards: false);
        var bytes = new byte[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
        {
            bytes[i] = tokens[i].Value;
        }

        return bytes;
    }

    /// <summary>Tokenizes a hex string into bytes; <c>??</c> (or <c>xx</c>) become wildcards when allowed.</summary>
    internal static List<(byte Value, bool IsWildcard)> Tokenize(string? text, bool allowWildcards)
    {
        var result = new List<(byte, bool)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var rawToken in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rawToken[2..] : rawToken;

            // "0x5" style single nibble → 0x05.
            if (token.Length == 1)
            {
                result.Add((ParseByte("0" + token, rawToken), false));
                continue;
            }

            if (token.Length == 0 || token.Length % 2 != 0)
            {
                throw new FormatException(
                    $"'{rawToken}' is not a valid hex byte sequence (expected pairs of hex digits such as \"05 0A FF\").");
            }

            for (int i = 0; i < token.Length; i += 2)
            {
                var pair = token.Substring(i, 2);
                if (pair is "??" or "xx" or "XX")
                {
                    if (!allowWildcards)
                    {
                        throw new FormatException(
                            $"Wildcard '{pair}' is only allowed in response \"expect\" patterns, not in payloads.");
                    }

                    result.Add((0, true));
                    continue;
                }

                result.Add((ParseByte(pair, rawToken), false));
            }
        }

        return result;
    }

    /// <summary>Formats bytes as upper-case, space separated hex: <c>05 0A 00 FF</c>.</summary>
    public static string Format(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Like <see cref="Format"/> but collapses trailing zero padding, which keeps 64-byte
    /// reports readable in the log: <c>05 0A 01 … (+60 × 00)</c>.
    /// </summary>
    public static string FormatCompact(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0)
        {
            end--;
        }

        int padding = bytes.Length - end;
        if (padding < 4)
        {
            return Format(bytes);
        }

        var head = Format(bytes[..end]);
        var tail = $"… (+{padding} × 00)";
        return head.Length == 0 ? tail : head + " " + tail;
    }

    /// <summary>
    /// Parses an integer written either as decimal (<c>"26000"</c>, <c>"-1"</c>) or hex (<c>"0x1A2B"</c>).
    /// Used for every numeric field in devices.json so VIDs, PIDs and masks can be written in hex.
    /// </summary>
    public static bool TryParseInteger(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        bool negative = s.StartsWith('-');
        if (negative)
        {
            s = s[1..];
        }

        bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);

        if (ok && negative)
        {
            value = -value;
        }

        return ok;
    }

    private static byte ParseByte(string pair, string context)
    {
        if (byte.TryParse(pair, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
        {
            return b;
        }

        throw new FormatException($"'{context}' contains invalid hex digits.");
    }
}
