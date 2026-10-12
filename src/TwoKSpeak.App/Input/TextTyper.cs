using System.Runtime.InteropServices;
using TwoKSpeak.App.Diagnostics;
using static TwoKSpeak.App.Input.NativeMethods;

namespace TwoKSpeak.App.Input;

/// <summary>
/// Types text into whatever has keyboard focus using Unicode key events, so diacritics work regardless of
/// keyboard layout and the clipboard is never touched. Windows blocks this into elevated (admin) windows
/// unless 2KSpeak itself runs elevated.
/// </summary>
public static class TextTyper
{
    private const int BatchChars = 64;

    /// <returns>False if Windows rejected the input (for example, focus is in an elevated window).</returns>
    public static bool Type(string text)
    {
        // Text typed while Windows thinks Ctrl, Alt or Win is held turns into shortcuts (Win+E opens Explorer for every
        // "e"). The shortcut's own keys are hidden from Windows, so a modifier it sees as held here is stuck.
        var stuck = StuckModifiers(vk => GetAsyncKeyState(vk) < 0);
        if (stuck.Count > 0)
        {
            KeyboardHook.SendKeys(ChordTracker.Releases(stuck));
            Log.Write($"released stuck keys before typing: {string.Join(", ", stuck.Select(vk => $"0x{vk:X2}"))}");
        }

        for (var start = 0; start < text.Length; start += BatchChars)
        {
            var chunk = text.AsSpan(start, Math.Min(BatchChars, text.Length - start));
            var inputs = new INPUT[chunk.Length * 2];
            for (var i = 0; i < chunk.Length; i++)
            {
                var c = chunk[i] == '\n' ? '\r' : chunk[i];
                inputs[2 * i] = Unicode(c, keyUp: false);
                inputs[2 * i + 1] = Unicode(c, keyUp: true);
            }
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Shift is left alone: Unicode key events ignore it, and it may be part of the user's chord.</summary>
    internal static IReadOnlyList<ushort> StuckModifiers(Func<ushort, bool> windowsSeesDown) =>
        Hotkey.ModifierKeys.Where(vk => Hotkey.GroupOf(vk) != Modifiers.Shift && windowsSeesDown(vk)).ToList();

    private static INPUT Unicode(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wScan = c,
            dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
            dwExtraInfo = OwnInputSignature,
        },
    };
}
