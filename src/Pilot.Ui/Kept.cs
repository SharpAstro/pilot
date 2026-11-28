namespace Pilot.Ui;

/// <summary>
/// A value built from some inputs, kept until one of them changes or, for a value that counts from the clock, until the second it
/// was built in turns over: a feed's page (<see cref="NodeFeed.Screen"/>, <see cref="NodeFeed.Flights"/>) is asked for several
/// times a look and many looks a second, while the lists it reads change only with the events that touch them (pilot #1443).
/// </summary>
/// <remarks>
/// The inputs are compared with <see cref="object.Equals(object?, object?)"/>: a list (an array) by reference, which is what a
/// feed keeps until an event changes it, a string or a record by value. Safe from any thread: an entry is written whole, and two
/// threads that miss at once each build, which costs a build and never a wrong value.
/// </remarks>
internal sealed class Kept<T>
    where T : class
{
    private sealed record Entry(object?[] Inputs, long? Second, T Value);

    private Entry? _last;

    /// <summary>The value for <paramref name="inputs"/>, built by <paramref name="build"/> unless the last one was built from the same inputs in the same second.</summary>
    /// <param name="now">The clock the value counts from, or null for one that does not count from it.</param>
    public T Get(DateTimeOffset? now, object?[] inputs, Func<T> build)
    {
        var second = now?.ToUnixTimeSeconds();
        if (Volatile.Read(ref _last) is { } last && last.Second == second && Same(last.Inputs, inputs))
        {
            return last.Value;
        }
        var value = build();
        Volatile.Write(ref _last, new Entry(inputs, second, value));
        return value;
    }

    private static bool Same(object?[] a, object?[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Length; i++)
        {
            if (!Equals(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }
}
