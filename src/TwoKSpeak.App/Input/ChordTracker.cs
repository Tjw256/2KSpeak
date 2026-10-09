namespace TwoKSpeak.App.Input;

public readonly record struct KeyStroke(ushort VirtualKey, bool Down);

public enum ChordSignal
{
    None,
    /// <summary>The chord was completed with nothing else held: start recording.</summary>
    Start,
    /// <summary>A chord key was released: stop recording and finish the dictation.</summary>
    Stop,
    /// <summary>Another key joined the chord (e.g. Win+Ctrl+→): discard the recording.</summary>
    Cancel,
}

/// <param name="Swallow">Hide this event from the rest of the system.</param>
/// <param name="Inject">Synthetic key events to send, in order.</param>
public sealed record HookDecision(bool Swallow, IReadOnlyList<KeyStroke> Inject, ChordSignal Signal)
{
    public static readonly HookDecision PassThrough = new(false, [], ChordSignal.None);
}

/// <summary>
/// Push-to-talk chord (Ctrl+Win) for a low-level keyboard hook. Pure state machine: feed it physical key
/// events, apply the returned decision.
/// While the chord is held its keys are hidden from Windows, which then believes no modifier is down. That is
/// what makes it safe to type the transcript while the user is still holding the keys — otherwise every
/// typed letter would become a Win+Ctrl shortcut. If another key joins, the hidden keys are re-pressed
/// first so shortcuts like Win+Ctrl+→ still work.
/// </summary>
public sealed class ChordTracker
{
    public const ushort LCtrl = 0xA2;
    public const ushort RCtrl = 0xA3;
    public const ushort LWin = 0x5B;
    public const ushort RWin = 0x5C;
    /// <summary>Unassigned key sent before a Win release so Windows does not open the Start menu.</summary>
    public const ushort MenuMask = 0xE8;

    private readonly HashSet<ushort> _chordKeysDown = [];
    private readonly HashSet<ushort> _otherKeysDown = [];
    private readonly HashSet<ushort> _hidden = [];
    private bool _recording;

    public bool IsRecording => _recording;

    private static bool IsCtrl(ushort vk) => vk is LCtrl or RCtrl;
    private static bool IsWin(ushort vk) => vk is LWin or RWin;
    private static bool IsChordKey(ushort vk) => IsCtrl(vk) || IsWin(vk);

    /// <param name="ownInjection">The event was sent by 2KSpeak itself (replayed keys, typed text).</param>
    public HookDecision Process(ushort vk, bool down, bool ownInjection)
    {
        if (ownInjection)
        {
            // Input from other tools (on-screen keyboard, automation) is treated like a physical key.
            return HookDecision.PassThrough;
        }
        if (IsChordKey(vk))
        {
            return down ? ChordKeyDown(vk) : ChordKeyUp(vk);
        }
        return down ? OtherKeyDown(vk) : OtherKeyUp(vk);
    }

    private HookDecision ChordKeyDown(ushort vk)
    {
        if (_hidden.Contains(vk))
        {
            return new HookDecision(true, [], ChordSignal.None); // auto-repeat of a hidden key
        }
        if (!_chordKeysDown.Add(vk))
        {
            return HookDecision.PassThrough; // auto-repeat of a visible key
        }

        var complete = _chordKeysDown.Any(IsCtrl) && _chordKeysDown.Any(IsWin);
        if (!complete || _recording || _hidden.Count > 0 || _otherKeysDown.Count > 0)
        {
            return HookDecision.PassThrough;
        }

        // Windows already saw the first chord key go down; release it there. This key is never shown.
        var inject = new List<KeyStroke>();
        foreach (var held in _chordKeysDown.Where(k => k != vk))
        {
            if (IsWin(held))
            {
                inject.Add(new KeyStroke(MenuMask, true));
                inject.Add(new KeyStroke(MenuMask, false));
            }
            inject.Add(new KeyStroke(held, false));
            _hidden.Add(held);
        }
        _hidden.Add(vk);
        _recording = true;
        return new HookDecision(true, inject, ChordSignal.Start);
    }

    private HookDecision ChordKeyUp(ushort vk)
    {
        _chordKeysDown.Remove(vk);
        if (!_hidden.Remove(vk))
        {
            return HookDecision.PassThrough;
        }
        if (_recording)
        {
            _recording = false;
            return new HookDecision(true, [], ChordSignal.Stop);
        }
        return new HookDecision(true, [], ChordSignal.None);
    }

    private HookDecision OtherKeyDown(ushort vk)
    {
        var repeat = !_otherKeysDown.Add(vk);
        if (_hidden.Count == 0 || repeat)
        {
            return HookDecision.PassThrough;
        }

        // Re-press the hidden keys that are still held, then replay this key after them.
        var inject = _hidden.Where(_chordKeysDown.Contains).Select(k => new KeyStroke(k, true)).ToList();
        inject.Add(new KeyStroke(vk, true));
        _hidden.Clear();
        var signal = _recording ? ChordSignal.Cancel : ChordSignal.None;
        _recording = false;
        return new HookDecision(true, inject, signal);
    }

    private HookDecision OtherKeyUp(ushort vk)
    {
        _otherKeysDown.Remove(vk);
        return HookDecision.PassThrough;
    }
}
