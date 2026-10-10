using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TwoKSpeak.App.Ui;

/// <summary>Windows 11 window chrome: dark title bar, rounded corners and a border in the app palette.</summary>
internal static partial class Dwm
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWCP_ROUND = 2;
    private const int Black = 0x000000;
    private const int Border = 0x1B1B1B; // COLORREF is 0x00BBGGRR; grey reads the same either way

    /// <summary>Call from SourceInitialized. Older Windows ignores the attributes it doesn't know.</summary>
    public static void Apply(Window window, bool roundCorners)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Set(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Set(handle, DWMWA_CAPTION_COLOR, Black);
        Set(handle, DWMWA_BORDER_COLOR, Border);
        if (roundCorners)
        {
            Set(handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        }
    }

    private static void Set(nint handle, int attribute, int value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
