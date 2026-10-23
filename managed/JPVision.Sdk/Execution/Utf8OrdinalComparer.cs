using System.Text;

namespace JPVision.Sdk.Execution;

/// <summary>Matches std::string ordering for valid UTF-8 JSON names, including supplementary characters.</summary>
internal sealed class Utf8OrdinalComparer : StringComparer
{
    public static Utf8OrdinalComparer Instance { get; } = new();
    private Utf8OrdinalComparer() { }

    public override int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        if (!HasSurrogate(x) && !HasSurrogate(y)) return string.CompareOrdinal(x, y);
        var left = x.EnumerateRunes().GetEnumerator();
        var right = y.EnumerateRunes().GetEnumerator();
        while (true)
        {
            bool hasLeft = left.MoveNext(), hasRight = right.MoveNext();
            if (!hasLeft || !hasRight) return hasLeft ? 1 : hasRight ? -1 : 0;
            int difference = left.Current.Value.CompareTo(right.Current.Value);
            if (difference != 0) return difference;
        }
    }

    public override bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);
    public override int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(obj);

    private static bool HasSurrogate(string text)
    {
        foreach (char value in text)
            if (char.IsSurrogate(value)) return true;
        return false;
    }
}
