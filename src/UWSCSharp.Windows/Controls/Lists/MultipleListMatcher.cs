namespace UWSCSharp.Windows;

internal sealed class MultipleListMatcher
{
    private readonly string[] terms;
    private readonly bool partial;

    internal MultipleListMatcher(string names, bool partial = false)
    {
        this.partial = partial;
        terms = [.. NativeListNameMatcher.AsciiUpper(names).Split('\t')
            .Select(term => partial ? term : NativeListControl.Trim(term))
            .Where(term => term.Length != 0)];
    }

    internal int Count => terms.Length;

    internal bool Matches(string item)
    {
        string candidate = NativeListNameMatcher.AsciiUpper(item);
        foreach (string term in terms)
        {
            if (NativeListNameMatcher.Compare(ref candidate, term, partial))
            {
                return true;
            }
        }

        return false;
    }

}
