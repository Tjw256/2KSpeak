using System.Runtime.InteropServices;
using System.Threading.Channels;
using TwoKSpeak.App.Diagnostics;
using static TwoKSpeak.App.Input.NativeMethods;

namespace TwoKSpeak.App.Input;

/// <summary>
/// Global low-level keyboard hook on its own thread. Windows silently removes a low-level hook whose callback
/// is slow, so the callback only runs the chord state machine and queues signals; it never waits on the UI
/// or on dictation work. Even so the thread can be starved (heavy load, a long pause), and Windows never says it
/// dropped the hook: the shortcut just stops working. So the thread also watches the keyboard through raw input,
/// which doesn't depend on the hook, and installs a new hook when a key reached raw input but not the hook.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>How long a key may go unseen by the hook before it counts as dropped.</summary>
    private static readonly TimeSpan DefaultDeadAfter = TimeSpan.FromSeconds(1);
    private const uint DropHookMessage = WM_APP;

    private readonly ChordTracker _tracker;
    private readonly Lock _trackerLock = new(); // hook thread vs. settings UI
    private readonly Channel<ChordSignal> _signals = Channel.CreateUnbounded<ChordSignal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly LowLevelKeyboardProc _callback; // kept alive: the hook holds only a native pointer
    private readonly Thread _thread;
    private readonly TaskCompletionSource _installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _deadAfterMs;
    private uint _threadId;
    private nint _hook;
    private long _events;
    private long _reinstalls;
    // Environment.TickCount64 values, only touched on the hook thread: the last hook call, and the first raw input
    // since then that had no hook call just before it (0 when there is none).
    private long _lastHookEvent;
    private long _unseenSince;

    public KeyboardHook(Modifiers chord) : this(chord, DefaultDeadAfter)
    {
    }

    internal KeyboardHook(Modifiers chord, TimeSpan deadAfter)
    {
        _deadAfterMs = (long)deadAfter.TotalMilliseconds;
        _tracker = new ChordTracker(chord);
        _callback = HookCallback;
        // Above normal work so a busy PC doesn't delay the callback past Windows' timeout.
        _thread = new Thread(Run) { IsBackground = true, Name = "2KSpeak keyboard hook", Priority = ThreadPriority.Highest };
        _thread.Start();
        _installed.Task.GetAwaiter().GetResult();
    }

    public ChannelReader<ChordSignal> Signals => _signals.Reader;

    /// <summary>Key events the hook has seen; for tests.</summary>
    internal long Events => Interlocked.Read(ref _events);

    /// <summary>Hooks installed after Windows dropped one; for tests.</summary>
    internal long Reinstalls => Interlocked.Read(ref _reinstalls);

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

        var watcher = WatchRawKeyboard();
        if (watcher == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            ThreadPool.QueueUserWorkItem(_ => Log.Write($"raw keyboard input unavailable ({error}); a dropped hook won't be noticed"));
        }
        var timer = SetTimer(0, 0, (uint)Math.Clamp(_deadAfterMs / 3, 50, 1000), 0);
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            // Hook callbacks run inside GetMessage. Posted here: raw input, the timer and the test's simulated drop.
            switch (message.message)
            {
                case WM_INPUT:
                    NoteRawInput();
                    DispatchMessageW(in message); // the window's default handling frees the raw input
                    break;
                case WM_TIMER:
                    Check();
                    break;
                case DropHookMessage:
                    UnhookWindowsHookEx(_hook);
                    break;
                default:
                    DispatchMessageW(in message);
                    break;
            }
        }
        KillTimer(0, timer);
        if (watcher != 0)
        {
            DestroyWindow(watcher);
        }
        UnhookWindowsHookEx(_hook);
    }

    /// <summary>A message-only window that receives every key press as raw input, even in the background.</summary>
    private static nint WatchRawKeyboard()
    {
        var window = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, GetModuleHandleW(null), 0);
        if (window == 0)
        {
            return 0;
        }
        var keyboard = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = window };
        if (!RegisterRawInputDevices([keyboard], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            DestroyWindow(window);
            return 0;
        }
        return window;
    }

    /// <summary>
    /// Windows calls the hook for a key just before it produces the key's raw input. Raw input without a recent hook
    /// call starts the clock; any hook call stops it.
    /// </summary>
    private void NoteRawInput()
    {
        var now = Environment.TickCount64;
        if (_unseenSince == 0 && now - _lastHookEvent > 100)
        {
            _unseenSince = now;
        }
    }

    /// <summary>
    /// A key reached raw input but the hook has seen nothing since: the hook is gone. Only then is it replaced, never in
    /// the middle of normal typing.
    /// </summary>
    private void Check()
    {
        var now = Environment.TickCount64;
        if (_unseenSince != 0 && now - _unseenSince >= _deadAfterMs)
        {
            _unseenSince = 0;
            var fresh = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
            if (fresh != 0)
            {
                UnhookWindowsHookEx(_hook);
                _hook = fresh;
                _lastHookEvent = now;
                Interlocked.Increment(ref _reinstalls);
                ThreadPool.QueueUserWorkItem(_ => Log.Write("keyboard hook was dropped by Windows; reinstalled"));
            }
        }
        // Windows records a key as down only after the hook returns, so leave keys from the last moment alone.
        if (now - _lastHookEvent > 250)
        {
            lock (_trackerLock)
            {
                if (!_tracker.IsRecording)
                {
                    _tracker.ForgetReleasedKeys(vk => GetAsyncKeyState(vk) < 0);
                }
            }
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
        _lastHookEvent = Environment.TickCount64;
        _unseenSince = 0;
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var message = (int)wParam;
        var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var own = (info.flags & LLKHF_INJECTED) != 0 && info.dwExtraInfo == OwnInputSignature;
        HookDecision decision;
        IReadOnlyList<ushort> stuck = [];
        lock (_trackerLock)
        {
            decision = _tracker.Process((ushort)info.vkCode, down, own);
            if (decision.Signal == ChordSignal.Stop)
            {
                stuck = _tracker.StuckChordKeys(vk => GetAsyncKeyState(vk) < 0);
            }
        }

        if (decision.Inject.Count > 0)
        {
            SendKeys(decision.Inject);
        }
        if (stuck.Count > 0)
        {
            // Windows got a chord key's press without asking the hook (it answered too late), so it would never get
            // the release and every later key would become a Win or Ctrl shortcut.
            SendKeys(ChordTracker.Releases(stuck));
            ThreadPool.QueueUserWorkItem(_ => Log.Write($"released stuck keys after the shortcut: {string.Join(", ", stuck.Select(vk => $"0x{vk:X2}"))}"));
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

    /// <summary>Sends key events marked as 2KSpeak's own, which the hook passes through untouched.</summary>
    internal static void SendKeys(IReadOnlyList<KeyStroke> strokes)
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
