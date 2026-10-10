using TwoKSpeak.App.Input;
using static TwoKSpeak.App.Input.ChordTracker;
using static TwoKSpeak.App.Input.Hotkey;

namespace TwoKSpeak.App.Tests;

public class ChordTrackerTests
{
    private const ushort KeyA = 0x41;
    private const ushort Right = 0x27;

    private readonly ChordTracker _tracker = new(Hotkey.Default);

    private HookDecision Down(ushort vk) => _tracker.Process(vk, down: true, ownInjection: false);
    private HookDecision Up(ushort vk) => _tracker.Process(vk, down: false, ownInjection: false);

    [Fact]
    public void CtrlThenWinStartsAndReleasesCtrlInWindows()
    {
        Assert.Equal(HookDecision.PassThrough, Down(LCtrl));

        var start = Down(LWin);

        Assert.True(start.Swallow);
        Assert.Equal(ChordSignal.Start, start.Signal);
        Assert.Equal([new KeyStroke(LCtrl, false)], start.Inject);
    }

    [Fact]
    public void WinThenCtrlMasksTheStartMenuBeforeReleasingWin()
    {
        Down(LWin);

        var start = Down(LCtrl);

        Assert.Equal(ChordSignal.Start, start.Signal);
        Assert.Equal([new KeyStroke(MenuMask, true), new KeyStroke(MenuMask, false), new KeyStroke(LWin, false)], start.Inject);
    }

    [Fact]
    public void HoldingHidesRepeatsAndReleasingStopsOnce()
    {
        Down(LCtrl);
        Down(LWin);

        Assert.True(Down(LCtrl).Swallow);                       // auto-repeat stays hidden
        Assert.True(Down(LWin).Swallow);

        var stop = Up(LWin);
        Assert.True(stop.Swallow);
        Assert.Equal(ChordSignal.Stop, stop.Signal);

        var second = Up(LCtrl);
        Assert.True(second.Swallow);                            // Windows already thinks Ctrl is up
        Assert.Equal(ChordSignal.None, second.Signal);
        Assert.False(_tracker.IsRecording);
    }

    [Fact]
    public void ThirdKeyCancelsAndReplaysTheShortcut()
    {
        Down(LWin);
        Down(LCtrl);

        var cancel = Down(Right);

        Assert.True(cancel.Swallow);
        Assert.Equal(ChordSignal.Cancel, cancel.Signal);
        Assert.Equal(3, cancel.Inject.Count);
        Assert.Contains(new KeyStroke(LWin, true), cancel.Inject);
        Assert.Contains(new KeyStroke(LCtrl, true), cancel.Inject);
        Assert.Equal(new KeyStroke(Right, true), cancel.Inject[^1]);  // replayed after the modifiers

        // After the replay everything is visible to Windows again.
        Assert.Equal(HookDecision.PassThrough, Up(Right));
        Assert.Equal(HookDecision.PassThrough, Up(LCtrl));
        Assert.Equal(HookDecision.PassThrough, Up(LWin));
    }

    [Fact]
    public void KeyAfterStopWhileOneChordKeyIsStillHeldRestoresIt()
    {
        Down(LCtrl);
        Down(LWin);
        Up(LCtrl);                                              // stop; Win still physically held but hidden

        var key = Down(KeyA);

        Assert.Equal(ChordSignal.None, key.Signal);
        Assert.Equal([new KeyStroke(LWin, true), new KeyStroke(KeyA, true)], key.Inject);
        Assert.Equal(HookDecision.PassThrough, Up(LWin));
    }

    [Fact]
    public void ChordDoesNotStartWhileAnotherKeyIsHeld()
    {
        Down(LShift);
        Down(LCtrl);

        var win = Down(LWin);

        Assert.Equal(HookDecision.PassThrough, win);           // Win+Ctrl+Shift+… shortcuts stay untouched
        Assert.False(_tracker.IsRecording);
    }

    [Fact]
    public void OwnInjectedInputIsIgnored()
    {
        Down(LCtrl);
        Down(LWin);

        var typed = _tracker.Process(0xE7, down: true, ownInjection: true); // VK_PACKET from the typer

        Assert.Equal(HookDecision.PassThrough, typed);
        Assert.True(_tracker.IsRecording);
    }

    [Fact]
    public void SingleModifiersPassThrough()
    {
        Assert.Equal(HookDecision.PassThrough, Down(LWin));
        Assert.Equal(HookDecision.PassThrough, Up(LWin));
        Assert.Equal(HookDecision.PassThrough, Down(RCtrl));
        Assert.Equal(HookDecision.PassThrough, Down(KeyA));
        Assert.Equal(HookDecision.PassThrough, Up(KeyA));
        Assert.Equal(HookDecision.PassThrough, Up(RCtrl));
    }

    [Fact]
    public void CanStartAgainAfterAFullRelease()
    {
        Down(LCtrl);
        Down(LWin);
        Up(LWin);
        Up(LCtrl);

        Down(RCtrl);
        Assert.Equal(ChordSignal.Start, Down(RWin).Signal);
    }

    [Fact]
    public void OtherChordsWorkAndAltReleaseIsMasked()
    {
        var tracker = new ChordTracker(Modifiers.Win | Modifiers.Alt);

        tracker.Process(LAlt, down: true, ownInjection: false);
        var start = tracker.Process(RWin, down: true, ownInjection: false);

        Assert.Equal(ChordSignal.Start, start.Signal);
        Assert.Equal([new KeyStroke(MenuMask, true), new KeyStroke(MenuMask, false), new KeyStroke(LAlt, false)], start.Inject);
        Assert.Equal(ChordSignal.Cancel, tracker.Process(LCtrl, down: true, ownInjection: false).Signal); // not part of this chord
    }

    [Fact]
    public void ThreeKeyChordNeedsAllThree()
    {
        var tracker = new ChordTracker(Modifiers.Ctrl | Modifiers.Win | Modifiers.Shift);

        tracker.Process(LCtrl, down: true, ownInjection: false);
        Assert.Equal(ChordSignal.None, tracker.Process(LWin, down: true, ownInjection: false).Signal);
        Assert.Equal(ChordSignal.Start, tracker.Process(LShift, down: true, ownInjection: false).Signal);
    }

    [Fact]
    public void CaptureHidesEverythingAndReportsThePeakChord()
    {
        _tracker.BeginCapture();

        Assert.True(Down(LCtrl).Swallow);
        Assert.True(Down(LAlt).Swallow);
        Assert.True(Down(LShift).Swallow);
        Assert.Null(Up(LShift).Capture);
        Assert.Null(Up(LAlt).Capture);
        var done = Up(LCtrl);

        Assert.True(done.Swallow);
        Assert.Equal(new CaptureOutcome(Modifiers.Ctrl | Modifiers.Alt | Modifiers.Shift), done.Capture);
        Assert.Equal(ChordSignal.None, done.Signal);
        Assert.Equal(HookDecision.PassThrough, Down(KeyA)); // capture is over
    }

    [Fact]
    public void CaptureEndsOnAnyReleaseAndRejectsLetters()
    {
        _tracker.BeginCapture();

        Assert.True(Down(LCtrl).Swallow);
        Assert.True(Down(KeyA).Swallow);
        Assert.Null(Up(KeyA).Capture);
        var done = Up(LCtrl);

        Assert.Equal(new CaptureOutcome(Modifiers.None), done.Capture);
        Assert.NotNull(Hotkey.Problem(done.Capture!.Chord!.Value));
        Assert.Equal(HookDecision.PassThrough, Down(KeyA)); // the keyboard is back to normal
    }

    [Fact]
    public void EscapeCancelsCapture()
    {
        _tracker.BeginCapture();

        var escape = Down(0x1B);

        Assert.True(escape.Swallow);
        Assert.Equal(new CaptureOutcome(null), escape.Capture);
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData(Modifiers.Ctrl | Modifiers.Win, null)]
    [InlineData(Modifiers.Win | Modifiers.Alt | Modifiers.Shift, null)]
    [InlineData(Modifiers.Ctrl, "Use at least two modifier keys")]
    [InlineData(Modifiers.Ctrl | Modifiers.Alt, "Ctrl+Alt is AltGr on many keyboard layouts")]
    [InlineData(Modifiers.Ctrl | Modifiers.Win | Modifiers.Alt | Modifiers.Shift, "Use at most three modifier keys")]
    public void ValidatesChords(Modifiers chord, string? problem)
    {
        Assert.Equal(problem, Hotkey.Problem(chord));
    }

    [Fact]
    public void FormatsInAFixedOrder()
    {
        Assert.Equal("Ctrl + Win", Hotkey.Format(Modifiers.Win | Modifiers.Ctrl));
    }
}
