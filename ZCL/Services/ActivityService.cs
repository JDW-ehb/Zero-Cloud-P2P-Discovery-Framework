namespace ZCM.Services;

public sealed class ActivityService
{
    private const int MaxEntries = 50;

    private readonly object _lock = new();
    private readonly List<string> _entries = [];

    /// <summary>Raised on the thread that called Log. Use MainThread dispatch in UI subscribers.</summary>
    public event Action<string>? EntryAdded;

    public void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";

        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveAt(_entries.Count - 1);
        }

        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<string> GetAll()
    {
        lock (_lock)
            return _entries.ToList();
    }
}