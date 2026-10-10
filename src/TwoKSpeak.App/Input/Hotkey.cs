namespace TwoKSpeak.App.Input;

/// <summary>Modifier groups a push-to-talk chord is built from (left and right keys count the same).</summary>
[Flags]
public enum Modifiers
{
    None = 0,
    Ctrl = 1,
    Win = 2,
    Alt = 4,
    Shift = 8,
}

public static class Hotkey
{
    public const Modifiers Default = Modifiers.Ctrl | Modifiers.Win;

    public const ushort LShift = 0xA0;
    public const ushort RShift = 0xA1;
    public const ushort LCtrl = 0xA2;
    public const ushort RCtrl = 0xA3;
    public const ushort LAlt = 0xA4;
    public const ushort RAlt = 0xA5;
    public const ushort LWin = 0x5B;
    public const ushort RWin = 0x5C;

    public static Modifiers GroupOf(ushort vk) => vk switch
    {
        LCtrl or RCtrl or 0x11 => Modifiers.Ctrl,
        LWin or RWin => Modifiers.Win,
        LAlt or RAlt or 0x12 => Modifiers.Alt,
        LShift or RShift or 0x10 => Modifiers.Shift,
        _ => Modifiers.None,
    };

    /// <summary>Why a chord can't be used, or null if it can.</summary>
    public static string? Problem(Modifiers chord)
    {
        var count = System.Numerics.BitOperations.PopCount((uint)chord);
        if (count < 2)
        {
            return "Use at least two modifier keys";
        }
        if (count > 3)
        {
            return "Use at most three modifier keys";
        }
        if (chord.HasFlag(Modifiers.Ctrl) && chord.HasFlag(Modifiers.Alt))
        {
            // AltGr reports Ctrl+Alt, so typing @ or € on Czech and other layouts would start dictation.
            return "Ctrl+Alt is AltGr on many keyboard layouts";
        }
        return null;
    }

    public static string Format(Modifiers chord) => string.Join(" + ",
        new[] { Modifiers.Ctrl, Modifiers.Win, Modifiers.Alt, Modifiers.Shift }.Where(m => chord.HasFlag(m)));
}
