using System.Runtime.InteropServices;
using TwoKSpeak.App.Input;
using static TwoKSpeak.App.Input.NativeMethods;

namespace TwoKSpeak.App.Tests;

/// <summary>Installs a real hook; presses F24, which nothing uses, so the test disturbs no other program.</summary>
public class KeyboardHookTests
{
    private const ushort F24 = 0x87;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(300);

    private static void PressF24()
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = F24 } },
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = F24, dwFlags = KEYEVENTF_KEYUP } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static async Task<bool> SeesPressAsync(KeyboardHook hook)
    {
        var before = hook.Events;
        PressF24();
        for (var i = 0; i < 20 && hook.Events < before + 2; i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        return hook.Events >= before + 2;
    }

    [Fact]
    public async Task DroppedHookSeesNothing()
    {
        // No reinstall during the test, so this shows the drop really cuts the hook off, like Windows does.
        using var hook = new KeyboardHook(Hotkey.Default, TimeSpan.FromHours(1));
        Assert.SkipUnless(await SeesPressAsync(hook), "No interactive desktop to send keys to.");

        hook.DropHookForTest();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(await SeesPressAsync(hook));
    }

    [Fact]
    public async Task DroppedHookComesBackByItself()
    {
        using var hook = new KeyboardHook(Hotkey.Default, Interval);
        Assert.SkipUnless(await SeesPressAsync(hook), "No interactive desktop to send keys to.");

        hook.DropHookForTest();
        await Task.Delay(Interval * 3, TestContext.Current.CancellationToken);

        Assert.True(await SeesPressAsync(hook), "The hook was not reinstalled.");
    }
}
