using System.Runtime.InteropServices;
using System.Threading.Channels;
using static TwoKSpeak.App.Input.NativeMethods;

namespace TwoKSpeak.App.Input;

/// <summary>
/// Global low-level keyboard hook on its own thread. Windows silently removes a low-level hook whose callback
/// is slow, so the callback only runs the chord state machine and queues signals; it never waits on the UI
/// or on dictation work. Even so the thread can be starved (heavy load, a long pause), and Windows never says it
/// dropped the hook: the shortcut just stops working. So the hook is replaced on a timer.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>A dropped hook is back within this long; a replacement costs two system calls.</summary>
    private static readonly TimeSpan DefaultReinstallInterval = TimeSpan.FromSeconds(15);
    private const uint DropHookMessage = WM_APP;

    private readonly ChordTracker _tracker;
    private readonly Lock _trackerLock = new(); // hook thread vs. settings UI
    private readonly Channel<ChordSignal> _signals = Channel.CreateUnbounded<ChordSignal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly LowLevelKeyboardProc _callback; // kept alive: the hook holds only a native pointer
    private readonly Thread _thread;
    private readonly TaskCompletionSource _installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _reinstallInterval;
    private uint _threadId;
    private nint _hook;
    private long _events;

    public KeyboardHook(Modifiers chord) : this(chord, DefaultReinstallInterval)
    {
    }

    internal KeyboardHook(Modifiers chord, TimeSpan reinstallInterval)
    {
        _reinstallInterval = reinstallInterval;
        _tracker = new ChordTracker(chord);
        _callback = HookCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "2KSpeak keyboard hook" };
        _thread.Start();
        _installed.Task.GetAwaiter().GetResult();
    }

    public ChannelReader<ChordSignal> Signals => _signals.Reader;

    /// <summary>Key events the hook has seen; for tests.</summary>
    internal long Events => Interlocked.Read(ref _events);

    /// <summary>Raised (on a pool thread) when a hotkey recording started by <see cref="BeginCapture"/> ends.</summary>
    public event Action<CaptureOutcome>? CaptureCompleted;

    public void SetChord(Modifiers chord)
    {
        lock (_trackerLock)
        {
            _tracker.Chord = chord;
        }
    }

    public void BeginCapture()
    {
        lock (_trackerLock)
        {
            _tracker.BeginCapture();
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
        if (_hook == 0)
        {
            _installed.SetException(new InvalidOperationException($"SetWindowsHookEx failed ({Marshal.GetLastPInvokeError})."));
            return;
        }
        _installed.SetResult();

        var timer = SetTimer(0, 0, (uint)_reinstallInterval.TotalMilliseconds, 0);
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            // Low-level hooks are delivered through this thread's message loop; posted messages are the timer and
            // the test's simulated drop.
            if (message.message == WM_TIMER)
            {
                Reinstall();
            }
            else if (message.message == DropHookMessage)
            {
                UnhookWindowsHookEx(_hook);
            }
        }
        KillTimer(0, timer);
        UnhookWindowsHookEx(_hook);
    }

    /// <summary>
    /// Installs a fresh hook, then removes the old one (if Windows hasn't already). Callbacks only run while this
    /// thread waits in GetMessage, so the swap can't interleave with one; the chord state lives in the tracker and
    /// carries over.
    /// </summary>
    private void Reinstall()
    {
        var fresh = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
        if (fresh != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = fresh;
        }
        lock (_trackerLock)
        {
            _tracker.ForgetReleasedKeys(vk => GetAsyncKeyState(vk) < 0);
        }
    }

    /// <summary>Removes the hook the way Windows does when it drops one; for tests.</summary>
    internal void DropHookForTest() => PostThreadMessageW(_threadId, DropHookMessage, 0, 0);

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode != HC_ACTION)
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        Interlocked.Increment(ref _events);
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var message = (int)wParam;
        var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var own = (info.flags & LLKHF_INJECTED) != 0 && info.dwExtraInfo == OwnInputSignature;
        HookDecision decision;
        lock (_trackerLock)
        {
            decision = _tracker.Process((ushort)info.vkCode, down, own);
        }

        if (decision.Inject.Count > 0)
        {
            SendKeys(decision.Inject);
        }
        if (decision.Signal != ChordSignal.None)
        {
            _signals.Writer.TryWrite(decision.Signal);
        }
        if (decision.Capture is { } capture)
        {
            ThreadPool.QueueUserWorkItem(_ => CaptureCompleted?.Invoke(capture));
        }
        return decision.Swallow ? 1 : CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void SendKeys(IReadOnlyList<KeyStroke> strokes)
    {
        var inputs = new INPUT[strokes.Count];
        for (var i = 0; i < strokes.Count; i++)
        {
            var vk = strokes[i].VirtualKey;
            var flags = strokes[i].Down ? 0u : KEYEVENTF_KEYUP;
            if (vk is Hotkey.RCtrl or Hotkey.RAlt or Hotkey.LWin or Hotkey.RWin)
            {
                flags |= KEYEVENTF_EXTENDEDKEY;
            }
            inputs[i] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags, dwExtraInfo = OwnInputSignature } };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _signals.Writer.TryComplete();
    }
}
