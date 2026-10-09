using System.Runtime.InteropServices;
using System.Threading.Channels;
using static TwoKSpeak.App.Input.NativeMethods;

namespace TwoKSpeak.App.Input;

/// <summary>
/// Global low-level keyboard hook on its own thread. Windows silently removes a low-level hook whose callback
/// is slow, so the callback only runs the chord state machine and queues signals; it never waits on the UI
/// or on dictation work.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly ChordTracker _tracker = new();
    private readonly Channel<ChordSignal> _signals = Channel.CreateUnbounded<ChordSignal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly LowLevelKeyboardProc _callback; // kept alive: the hook holds only a native pointer
    private readonly Thread _thread;
    private readonly TaskCompletionSource _installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;
    private nint _hook;

    public KeyboardHook()
    {
        _callback = HookCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "2KSpeak keyboard hook" };
        _thread.Start();
        _installed.Task.GetAwaiter().GetResult();
    }

    public ChannelReader<ChordSignal> Signals => _signals.Reader;

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

        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            // Low-level hooks are delivered through this thread's message loop; nothing else to dispatch.
            _ = message;
        }
        UnhookWindowsHookEx(_hook);
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode != HC_ACTION)
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var message = (int)wParam;
        var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var own = (info.flags & LLKHF_INJECTED) != 0 && info.dwExtraInfo == OwnInputSignature;
        var decision = _tracker.Process((ushort)info.vkCode, down, own);

        if (decision.Inject.Count > 0)
        {
            SendKeys(decision.Inject);
        }
        if (decision.Signal != ChordSignal.None)
        {
            _signals.Writer.TryWrite(decision.Signal);
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
            if (vk is ChordTracker.RCtrl or ChordTracker.LWin or ChordTracker.RWin)
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
