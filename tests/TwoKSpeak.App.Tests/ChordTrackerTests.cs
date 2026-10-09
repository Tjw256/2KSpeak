using TwoKSpeak.App.Input;
using static TwoKSpeak.App.Input.ChordTracker;

namespace TwoKSpeak.App.Tests;

public class ChordTrackerTests
{
    private const ushort KeyA = 0x41;
    private const ushort Right = 0x27;
    private const ushort LShift = 0xA0;

    private readonly ChordTracker _tracker = new();

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
}
