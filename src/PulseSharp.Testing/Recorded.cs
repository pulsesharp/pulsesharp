using System.Text;

namespace PulseSharp.Testing;

internal sealed record RecordedItem<T>(T Value, string Where);

internal sealed class Recorded<T>(string kind, string verb, IReadOnlyList<RecordedItem<T>> items, string scope, string others)
{
    private const int ShownItems = 10;

    public IReadOnlyList<RecordedItem<T>> Items { get; } = items;

    public Recorded<T> Narrowed(Func<RecordedItem<T>, bool> keep, string narrowedScope)
    {
        return new Recorded<T>(kind, verb, Items.Where(keep).ToList(), narrowedScope, others);
    }

    public T One(Func<T, bool>? where, string? condition)
    {
        var matches = Matching(where);
        if (matches.Count != 1)
        {
            throw new PulseAssertionException(Expected("one", where, condition, matches.Count));
        }

        return matches[0];
    }

    public void None(Func<T, bool>? where, string? condition)
    {
        var matches = Matching(where);
        if (matches.Count != 0)
        {
            throw new PulseAssertionException(Expected("no", where, condition, matches.Count));
        }
    }

    public IReadOnlyList<T> Exactly(int count, Func<T, bool>? where, string? condition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var matches = Matching(where);
        if (matches.Count != count)
        {
            throw new PulseAssertionException(Expected(count.ToString(System.Globalization.CultureInfo.InvariantCulture), where, condition, matches.Count));
        }

        return matches;
    }

    private List<T> Matching(Func<T, bool>? where)
    {
        return Items.Select(item => item.Value).Where(value => where is null || where(value)).ToList();
    }

    private string Expected(string amount, Func<T, bool>? where, string? condition, int found)
    {
        var message = new StringBuilder()
            .Append("Expected ").Append(amount).Append(' ').Append(kind);

        if (where is not null)
        {
            message.Append(" where ").Append(condition ?? "the condition holds");
        }

        message.Append(scope).Append(", but found ").Append(found == 0 ? "none" : found.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('.');
        message.AppendLine();

        if (Items.Count == 0)
        {
            message.Append("No ").Append(kind).Append(" was ").Append(verb).Append(scope).Append('.');
        }
        else
        {
            message.Append(Capitalized(verb)).Append(' ').Append(kind).Append(scope).Append(':');
            foreach (var item in Items.Take(ShownItems))
            {
                message.AppendLine().Append("  ").Append(item.Value).Append(item.Where);
            }

            if (Items.Count > ShownItems)
            {
                message.AppendLine().Append("  ... and ").Append(Items.Count - ShownItems).Append(" more");
            }
        }

        if (others.Length > 0)
        {
            message.AppendLine().Append(others);
        }

        return message.ToString();
    }

    private static string Capitalized(string text)
    {
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
