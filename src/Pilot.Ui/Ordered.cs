namespace Pilot.Ui;

/// <summary>Lists already in one order, merged in it.</summary>
internal static class Ordered
{
    /// <summary>
    /// <paramref name="lists"/>, each already in the order <paramref name="comparer"/> gives, merged into one in that order. A tie
    /// goes to the earlier list, and within a list the lines keep their places, so the result is what a stable sort of the lists'
    /// concatenation gives (<see cref="System.Linq.Enumerable.OrderBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/>),
    /// at the cost of one pass instead of a sort: a window of several nodes sorted thousands of session lines at every look
    /// (pilot #1443).
    /// </summary>
    public static List<T> Merge<T>(IReadOnlyList<IReadOnlyList<T>> lists, IComparer<T> comparer)
    {
        var merged = new List<T>(lists.Sum(l => l.Count));
        var at = new int[lists.Count];
        while (true)
        {
            var from = -1;
            for (var i = 0; i < lists.Count; i++)
            {
                if (at[i] < lists[i].Count && (from < 0 || comparer.Compare(lists[i][at[i]], lists[from][at[from]]) < 0))
                {
                    from = i;
                }
            }
            if (from < 0)
            {
                return merged;
            }
            merged.Add(lists[from][at[from]++]);
        }
    }
}
