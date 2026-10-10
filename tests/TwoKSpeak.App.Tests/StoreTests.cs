using TwoKSpeak.App.Input;
using TwoKSpeak.App.Settings;

namespace TwoKSpeak.App.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "2kspeak-tests-" + Guid.NewGuid().ToString("N"));

    private string PathOf(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void SettingsRoundTripAndAnnounceOnlyRealChanges()
    {
        var store = new SettingsStore(PathOf("settings.json"));
        var changes = new List<(AppSettings Previous, AppSettings Current)>();
        store.Changed += (previous, current) => changes.Add((previous, current));

        store.Update(s => s with { Hotkey = Modifiers.Win | Modifiers.Alt, Device = RecognitionDevice.Cpu, Microphone = "USB Mic" });
        store.Update(s => s with { Device = RecognitionDevice.Cpu });

        Assert.Single(changes);
        var reloaded = new SettingsStore(PathOf("settings.json")).Current;
        Assert.Equal(store.Current, reloaded);
        Assert.Equal(Modifiers.Win | Modifiers.Alt, reloaded.Hotkey);
        Assert.Contains("\"Win, Alt\"", File.ReadAllText(PathOf("settings.json")));
    }

    [Fact]
    public void HandEditedSettingsAreClampedAndBadHotkeysReset()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf("settings.json"), """{ "Hotkey": "Ctrl, Alt", "PauseMs": 5, "IdleUnloadMinutes": -3 }""");

        var settings = new SettingsStore(PathOf("settings.json")).Current;

        Assert.Equal(Hotkey.Default, settings.Hotkey);
        Assert.Equal(AppSettings.PauseMinMs, settings.PauseMs);
        Assert.Equal(0, settings.IdleUnloadMinutes);
    }

    [Fact]
    public void BrokenSettingsFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf("settings.json"), "{ not json");

        Assert.Equal(new AppSettings(), new SettingsStore(PathOf("settings.json")).Current);
    }

    [Fact]
    public void HistoryKeepsTheNewestFiveAndSurvivesARestart()
    {
        var history = new HistoryStore(PathOf("history.json"), save: true);
        var start = DateTimeOffset.Now;
        for (var i = 1; i <= 7; i++)
        {
            history.Add($"text {i}", start.AddSeconds(i));
        }

        Assert.Equal(["text 7", "text 6", "text 5", "text 4", "text 3"], history.Entries.Select(e => e.Text));
        Assert.Equal(history.Entries, new HistoryStore(PathOf("history.json"), save: true).Entries);
    }

    [Fact]
    public void TurningSavingOffDeletesTheFileButKeepsTheSession()
    {
        var history = new HistoryStore(PathOf("history.json"), save: true);
        history.Add("private", DateTimeOffset.Now);
        Assert.True(File.Exists(PathOf("history.json")));

        history.SetSaving(false);
        history.Add("also private", DateTimeOffset.Now);

        Assert.False(File.Exists(PathOf("history.json")));
        Assert.Equal(2, history.Entries.Count);
    }

    [Fact]
    public void UnsavedHistoryNeverTouchesAMissingFolder()
    {
        var history = new HistoryStore(PathOf("history.json"), save: false);

        history.Add("text", DateTimeOffset.Now);
        history.Clear();

        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void ClearRemovesEntriesAndFile()
    {
        var history = new HistoryStore(PathOf("history.json"), save: true);
        history.Add("text", DateTimeOffset.Now);

        history.Clear();

        Assert.Empty(history.Entries);
        Assert.False(File.Exists(PathOf("history.json")));
    }
}
