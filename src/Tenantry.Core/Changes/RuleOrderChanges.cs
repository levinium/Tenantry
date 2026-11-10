namespace Tenantry.Core.Changes;

/// <summary>A mail flow rule in an order: its Exchange identity and name.</summary>
public sealed record RuleSlot(string Identity, string Name);

/// <summary>
/// A new order for the mail flow rules, as one change however many rules move. Exchange only moves
/// one rule at a time (Set-TransportRule -Priority, renumbering the rest each time), so applying it
/// sends just the moves needed (<see cref="Moves"/>) one after another, then reads the order back.
/// </summary>
/// <param name="Rules">The change's target: the organization's rule order (kind "ruleorder").</param>
public sealed record ReorderRules(ChangeTarget Rules, IReadOnlyList<RuleSlot> Old, IReadOnlyList<RuleSlot> New) : ChangeOp
{
    public const string Kind = "ruleorder";

    /// <summary>The one target every reorder fills, so arranging again replaces the pending order.</summary>
    public static ChangeTarget OrderTarget => new(Kind, "order", "Mail Flow Rule Order", null);

    public override ChangeTarget Target => Rules;
    public override string Key => "order";
    public override string Label => "Mail Flow Rule Order";

    public override string Before => Describe(Old, Old);
    public override string After => Describe(New, Old);

    public override bool IsNoChange => Old.Select(r => r.Identity).SequenceEqual(New.Select(r => r.Identity), StringComparer.Ordinal);

    /// <summary>Every rule Exchange is asked to move (the guard checks each; the rest only shift around them).</summary>
    public override IReadOnlyList<ChangeTarget> Guarded =>
        [.. Moves().Select(m => new ChangeTarget("rule", m.Identity, New.First(r => r.Identity == m.Identity).Name, null))];

    public override ChangeOp Since(ChangeOp earlier) => earlier is ReorderRules e ? this with { Old = e.Old } : this;

    /// <summary>
    /// The fewest moves that turn <see cref="Old"/> into <see cref="New"/>. The longest run of rules
    /// already in the right order among themselves stays put; each other rule, taken in the new
    /// order, is moved to just after the rule before it. That is what Set-TransportRule -Priority does
    /// (the rule lands at that place and the rest shift), so moving one rule, even top to bottom, is
    /// one call.
    /// </summary>
    public IReadOnlyList<(string Identity, int Priority)> Moves()
    {
        var oldIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Old.Count; i++)
            oldIndex[Old[i].Identity] = i;
        var wanted = New.Select(r => r.Identity).Where(oldIndex.ContainsKey).ToList();
        var stay = LongestInOrder(wanted.Select(id => oldIndex[id]).ToList()).Select(i => wanted[i]).ToHashSet(StringComparer.Ordinal);

        var current = Old.Select(r => r.Identity).ToList();
        var moves = new List<(string, int)>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var id = wanted[i];
            if (stay.Contains(id))
                continue;
            current.Remove(id);
            var to = i == 0 ? 0 : current.IndexOf(wanted[i - 1]) + 1;
            current.Insert(to, id);
            moves.Add((id, to));
        }
        return moves;
    }

    /// <summary>The positions (in <paramref name="values"/>) of a longest increasing run, in order.</summary>
    static List<int> LongestInOrder(List<int> values)
    {
        var length = new int[values.Count];
        var previous = new int[values.Count];
        var best = -1;
        for (var i = 0; i < values.Count; i++)
        {
            length[i] = 1;
            previous[i] = -1;
            for (var j = 0; j < i; j++)
                if (values[j] < values[i] && length[j] + 1 > length[i])
                {
                    length[i] = length[j] + 1;
                    previous[i] = j;
                }
            if (best < 0 || length[i] > length[best])
                best = i;
        }
        var run = new List<int>();
        for (var i = best; i >= 0; i = previous[i])
            run.Insert(0, i);
        return run;
    }

    /// <summary>"Copy finance mail to auditors 1st, Board footer 4th (3 rules move)".</summary>
    string Describe(IReadOnlyList<RuleSlot> order, IReadOnlyList<RuleSlot> before)
    {
        var moved = order.Select((r, i) => (r, i)).Where(x => before.FindIndex(b => b.Identity == x.r.Identity) != x.i).ToArray();
        if (moved.Length == 0)
            return $"{order.Count} rules in order";
        var shown = string.Join(", ", moved.Take(4).Select(x => $"{x.r.Name} {Ordinal(x.i + 1)}"));
        return moved.Length > 4 ? $"{shown} and {moved.Length - 4} more" : shown;
    }

    static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}

static class SlotListExtensions
{
    public static int FindIndex(this IReadOnlyList<RuleSlot> list, Func<RuleSlot, bool> match)
    {
        for (var i = 0; i < list.Count; i++)
            if (match(list[i]))
                return i;
        return -1;
    }
}
