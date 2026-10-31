namespace Alcifra.UiEndToEndTests;

// What a fake server received, in order. Its listener appends from its own thread while the client under test may still
// be sending, so a test that enumerated a live List met "Collection was modified" (the 0.14.0 Messenger client follows a
// direct message with a read of its own). Every read is a copy taken under the lock, and so is every write's partner.
public sealed class RequestLog<T>
{
    private readonly List<T> _items = [];

    public void Add(T item)
    {
        lock (_items)
        {
            _items.Add(item);
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_items)
        {
            return [.. _items];
        }
    }
}
