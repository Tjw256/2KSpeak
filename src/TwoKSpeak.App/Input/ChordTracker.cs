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

/// <summary>Result of recording a new hotkey: the modifiers held at the peak, or null when Esc cancelled.</summary>
public sealed record CaptureOutcome(Modifiers? Chord);

/// <param name="Swallow">Hide this event from the rest of the system.</param>
/// <param name="Inject">Synthetic key events to send, in order.</param>
/// <param name="Capture">Set when a hotkey recording finished with this event.</param>
public sealed record HookDecision(bool Swallow, IReadOnlyList<KeyStroke> Inject, ChordSignal Signal, CaptureOutcome? Capture = null)
{
    public static readonly HookDecision PassThrough = new(false, [], ChordSignal.None);
    public static readonly HookDecision Hide = new(true, [], ChordSignal.None);
}

/// <summary>
/// Push-to-talk chord (default Ctrl+Win) for a low-level keyboard hook. Pure state machine: feed it physical
/// key events, apply the returned decision.
/// While the chord is held its keys are hidden from Windows, which then believes no modifier is down. That is
/// what makes it safe to type the transcript while the user is still holding the keys — otherwise every
/// typed letter would become a shortcut. If another key joins, the hidden keys are re-pressed first so
/// shortcuts like Win+Ctrl+→ still work.
/// </summary>
public sealed class ChordTracker
{
    /// <summary>Unassigned key sent before a Win or Alt release so Windows opens neither Start nor a menu bar.</summary>
    public const ushort MenuMask = 0xE8;
    private const ushort Escape = 0x1B;

    private readonly HashSet<ushort> _chordKeysDown = [];
    private readonly HashSet<ushort> _otherKeysDown = [];
    private readonly HashSet<ushort> _hidden = [];
    private bool _recording;

    private bool _capturing;
    private readonly HashSet<ushort> _captureDown = [];
    private Modifiers _capturePeak;
    private bool _captureOtherKey;

    public ChordTracker(Modifiers chord)
    {
        Chord = chord;
    }

    /// <summary>The chord to listen for. Change it only while nothing is held (from the settings UI).</summary>
    public Modifiers Chord { get; set; }

    public bool IsRecording => _recording;

    /// <summary>
    /// Starts recording a new hotkey: every key is hidden from Windows until all are released (so the
    /// candidate chord triggers nothing), and the modifiers held at the peak are reported. Esc cancels.
    /// </summary>
    public void BeginCapture()
    {
        _capturing = true;
        _captureDown.Clear();
        _capturePeak = Modifiers.None;
        _captureOtherKey = false;
    }

    private bool IsChordKey(ushort vk) => (Chord & Hotkey.GroupOf(vk)) != 0;

    /// <param name="ownInjection">The event was sent by 2KSpeak itself (replayed keys, typed text).</param>
    public HookDecision Process(ushort vk, bool down, bool ownInjection)
    {
        if (ownInjection)
        {
            // Input from other tools (on-screen keyboard, automation) is treated like a physical key.
            return HookDecision.PassThrough;
        }
        if (_capturing)
        {
            return CaptureKey(vk, down);
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
            return HookDecision.Hide; // auto-repeat of a hidden key
        }
        if (!_chordKeysDown.Add(vk))
        {
            return HookDecision.PassThrough; // auto-repeat of a visible key
        }

        var held = _chordKeysDown.Aggregate(Modifiers.None, (all, k) => all | Hotkey.GroupOf(k));
        if (held != Chord || _recording || _hidden.Count > 0 || _otherKeysDown.Count > 0)
        {
            return HookDecision.PassThrough;
        }

        // Windows already saw the earlier chord keys go down; release them there. This key is never shown.
        var inject = new List<KeyStroke>();
        foreach (var earlier in _chordKeysDown.Where(k => k != vk))
        {
            if (Hotkey.GroupOf(earlier) is Modifiers.Win or Modifiers.Alt)
            {
                inject.Add(new KeyStroke(MenuMask, true));
                inject.Add(new KeyStroke(MenuMask, false));
            }
            inject.Add(new KeyStroke(earlier, false));
            _hidden.Add(earlier);
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
        return HookDecision.Hide;
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

    private HookDecision CaptureKey(ushort vk, bool down)
    {
        if (down && vk == Escape)
        {
            _capturing = false;
            return HookDecision.Hide with { Capture = new CaptureOutcome(null) };
        }

        if (down)
        {
            _captureDown.Add(vk);
            _capturePeak |= Hotkey.GroupOf(vk);
            _captureOtherKey |= Hotkey.GroupOf(vk) == Modifiers.None;
            return HookDecision.Hide;
        }

        _captureDown.Remove(vk);
        if (_captureDown.Count > 0)
        {
            return HookDecision.Hide;
        }
        // Any full press-and-release ends the recording, so a stray letter can't keep the keyboard swallowed.
        // A combination with a letter in it is reported as no modifiers, which the caller rejects.
        _capturing = false;
        return HookDecision.Hide with { Capture = new CaptureOutcome(_captureOtherKey ? Modifiers.None : _capturePeak) };
    }
}
