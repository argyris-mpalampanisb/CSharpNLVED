namespace CSharpNLVED.Core;

/// <summary>
/// Compares strings the way people expect: "clip2" before "clip10", and camera names
/// like GX010023 / GX010024 in recording order. Case-insensitive.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                // Compare whole numbers: skip leading zeros, then longer number = bigger.
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length)
                    return a.Length.CompareTo(b.Length);

                var digits = a.SequenceCompareTo(b);
                if (digits != 0)
                    return Math.Sign(digits);
            }
            else
            {
                var c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                if (c != 0)
                    return c;
                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
