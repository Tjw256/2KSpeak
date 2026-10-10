using System.Text.Json;
using TwoKSpeak.App.Diagnostics;

namespace TwoKSpeak.App.Settings;

public sealed record HistoryEntry(string Text, DateTimeOffset Time);

/// <summary>
/// The last few finished dictations for the tray. Kept on disk only while saving is enabled; turning it off
/// deletes the file so no transcript text is left behind.
/// </summary>
public sealed class HistoryStore
{
    public const int Capacity = 5;
    private readonly string _path;
    private readonly Lock _gate = new();
    private List<HistoryEntry> _entries;
    private bool _save;

    public HistoryStore(string path, bool save)
    {
        _path = path;
        _save = save;
        _entries = save ? Load(path) : [];
    }

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event Action? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public void Add(string text, DateTimeOffset time)
    {
        lock (_gate)
        {
            _entries.Insert(0, new HistoryEntry(text, time));
            if (_entries.Count > Capacity)
            {
                _entries.RemoveRange(Capacity, _entries.Count - Capacity);
            }
            Persist();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Persist();
        }
        Changed?.Invoke();
    }

    public void SetSaving(bool save)
    {
        lock (_gate)
        {
            _save = save;
            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            if (!_save || _entries.Count == 0)
            {
                if (File.Exists(_path)) // Delete throws when the folder doesn't exist yet
                {
                    File.Delete(_path);
                }
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch (IOException ex)
        {
            Log.Write($"history not saved: {ex.Message}");
        }
    }

    private static List<HistoryEntry> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path)) ?? []).Take(Capacity).ToList()
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Write($"history unreadable, starting empty: {ex.Message}");
            return [];
        }
    }
}
