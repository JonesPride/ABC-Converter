using System.Collections.Generic;

namespace ABC.Converter.Core.Sorting;

public sealed class NaturalPathComparer : IComparer<string>
{
    public static NaturalPathComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        string a = left.Replace('\\', '/');
        string b = right.Replace('\\', '/');
        int ai = 0, bi = 0;

        while (ai < a.Length && bi < b.Length)
        {
            if (char.IsDigit(a[ai]) && char.IsDigit(b[bi]))
            {
                int aStart = ai, bStart = bi;
                while (ai < a.Length && char.IsDigit(a[ai])) ai++;
                while (bi < b.Length && char.IsDigit(b[bi])) bi++;

                string aDigits = a[aStart..ai].TrimStart('0');
                string bDigits = b[bStart..bi].TrimStart('0');
                if (aDigits.Length == 0) aDigits = "0";
                if (bDigits.Length == 0) bDigits = "0";

                int lengthResult = aDigits.Length.CompareTo(bDigits.Length);
                if (lengthResult != 0) return lengthResult;
                int numberResult = string.CompareOrdinal(aDigits, bDigits);
                if (numberResult != 0) return numberResult;
                continue;
            }

            int charResult = char.ToUpperInvariant(a[ai]).CompareTo(char.ToUpperInvariant(b[bi]));
            if (charResult != 0) return charResult;
            ai++;
            bi++;
        }

        int pathLengthResult = a.Length.CompareTo(b.Length);
        return pathLengthResult != 0
            ? pathLengthResult
            : string.CompareOrdinal(left, right);
    }
}
