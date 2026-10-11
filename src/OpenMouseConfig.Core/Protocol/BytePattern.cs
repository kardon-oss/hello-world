namespace OpenMouseConfig.Core.Protocol;

/// <summary>
/// A hex prefix pattern with <c>??</c> wildcards, e.g. <c>"05 81 ?? 01"</c>.
/// Used by response definitions (<c>"expect"</c>) to recognise the report that answers a command,
/// so unrelated input reports (mouse movement, other notifications) are skipped.
/// </summary>
public sealed class BytePattern
{
    private readonly byte[] _values;
    private readonly bool[] _wildcards;

    private BytePattern(byte[] values, bool[] wildcards)
    {
        _values = values;
        _wildcards = wildcards;
    }

    /// <summary>Pattern that matches any report.</summary>
    public static BytePattern Any { get; } = new([], []);

    public int Length => _values.Length;

    public static BytePattern Parse(string? text)
    {
        var tokens = HexFormat.Tokenize(text, allowWildcards: true);
        return tokens.Count == 0
            ? Any
            : new BytePattern(tokens.Select(t => t.Value).ToArray(), tokens.Select(t => t.IsWildcard).ToArray());
    }

    /// <summary>True when <paramref name="report"/> starts with this pattern.</summary>
    public bool Matches(ReadOnlySpan<byte> report)
    {
        if (report.Length < _values.Length)
        {
            return false;
        }

        for (int i = 0; i < _values.Length; i++)
        {
            if (!_wildcards[i] && report[i] != _values[i])
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() =>
        string.Join(' ', _values.Select((v, i) => _wildcards[i] ? "??" : v.ToString("X2")));
}
