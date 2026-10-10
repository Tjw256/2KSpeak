namespace TwoKSpeak.App.Dictation;

/// <summary>Forwards dictation feedback to the overlay and reports when listening starts and stops (tray icon, flyout).</summary>
public sealed class ListeningView(IDictationView inner, Action<bool> listeningChanged) : IDictationView
{
    public void ShowListening()
    {
        inner.ShowListening();
        listeningChanged(true);
    }

    public void ShowFinishing()
    {
        inner.ShowFinishing();
        listeningChanged(false);
    }

    public void ShowPreview(string text) => inner.ShowPreview(text);

    // Errors can arrive mid-hold (e.g. typing rejected); listening ends only with the hold.
    public void ShowError(string message) => inner.ShowError(message);

    public void Hide()
    {
        inner.Hide();
        listeningChanged(false);
    }
}
