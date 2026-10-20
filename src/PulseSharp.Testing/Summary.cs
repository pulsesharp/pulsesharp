namespace PulseSharp.Testing;

internal static class Summary
{
    public static string Others(string kind, IEnumerable<string> names)
    {
        var counts = names
            .GroupBy(name => name, StringComparer.Ordinal)
            .Select(group => $"{group.Key} ({group.Count()})")
            .ToList();

        return counts.Count == 0 ? string.Empty : $"Other {kind}: {string.Join(", ", counts)}.";
    }
}
