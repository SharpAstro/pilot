using System.Collections;

namespace Pilot.Node.Tests;

/// <summary>
/// A list a fake records calls into: every add, read and enumeration takes one lock, so calls made in parallel are each kept
/// exactly once and a test reading while a call runs sees a whole snapshot (PIL721).
/// </summary>
internal sealed class Recorded<T> : IReadOnlyList<T>
{
    private readonly List<T> _items = [];

    public void Add(T item)
    {
        lock (_items)
        {
            _items.Add(item);
        }
    }

    public void Clear()
    {
        lock (_items)
        {
            _items.Clear();
        }
    }

    public int Count
    {
        get
        {
            lock (_items)
            {
                return _items.Count;
            }
        }
    }

    public T this[int index]
    {
        get
        {
            lock (_items)
            {
                return _items[index];
            }
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        T[] snapshot;
        lock (_items)
        {
            snapshot = [.. _items];
        }
        return ((IEnumerable<T>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
