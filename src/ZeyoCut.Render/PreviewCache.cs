// SPDX-License-Identifier: MIT

using SkiaSharp;

namespace ZeyoCut.Render;

// A frame render_frame showed: the full-size PNG it saves, the warnings drawing it gave, and the
// smaller PNG it returned last (and at which width).
public sealed record CachedFrame(byte[] Png, IReadOnlyList<string> Warnings, int ScaledWidth = 0, byte[]? Scaled = null)
{
    public long Bytes => Png.Length + (Scaled?.Length ?? 0);
}

// A contact sheet cell, at its size in the sheet.
public sealed record CachedCell(SKImage Image, IReadOnlyList<string> Warnings) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

// Finished previews, so showing a frame again takes no drawing or PNG encoding while nothing it shows
// has changed. Keys start with the frame's FrameKeys key (every drawn layer with its animated values
// at that frame, where it lands, the files it reads and the clip frame it shows), so an edit at 10 s
// leaves the previews at 2 s as they were. Past the budget, the ones used longest ago are dropped.
public sealed class PreviewCache : IDisposable
{
    public const long DefaultBudget = 128L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly long _budget;
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    public PreviewCache(long budget = DefaultBudget)
    {
        _budget = budget;
    }

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    public T? Get<T>(string key) where T : class
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node) && node.Value.Value is T value)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                Hits++;
                return value;
            }

            Misses++;
            return null;
        }
    }

    // Keeps the value under the key in place of an older one, and drops the ones used longest ago until
    // the rest fit the budget (never the one just added). A dropped value that is disposable is disposed.
    public T Add<T>(string key, T value, long bytes) where T : class
    {
        lock (_gate)
        {
            if (_index.Remove(key, out var old))
            {
                _lru.Remove(old);
                Drop(old.Value, dispose: !ReferenceEquals(old.Value.Value, value));
            }

            _index[key] = _lru.AddFirst(new Entry(key, value, bytes));
            _bytes += bytes;
            while (_bytes > _budget && _lru.Last is { } last && last != _lru.First)
            {
                _lru.RemoveLast();
                _index.Remove(last.Value.Key);
                Drop(last.Value, dispose: true);
            }

            return value;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _lru)
            {
                (entry.Value as IDisposable)?.Dispose();
            }

            _lru.Clear();
            _index.Clear();
            _bytes = 0;
        }
    }

    private void Drop(Entry entry, bool dispose)
    {
        _bytes -= entry.Bytes;
        if (dispose)
        {
            (entry.Value as IDisposable)?.Dispose();
        }
    }

    private sealed record Entry(string Key, object Value, long Bytes);
}
