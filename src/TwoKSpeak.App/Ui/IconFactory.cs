using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TwoKSpeak.App.Ui;

/// <summary>Renders vector artwork into a Windows icon (PNG-compressed ICO entries) for the tray.</summary>
public static class IconFactory
{
    public static System.Drawing.Icon FromDrawing(ImageSource drawing, params int[] sizes)
    {
        var pngs = sizes.Select(size => (Size: size, Png: RenderPng(drawing, size))).ToList();
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((short)0);           // reserved
            writer.Write((short)1);           // type: icon
            writer.Write((short)pngs.Count);
            var offset = 6 + 16 * pngs.Count;
            foreach (var (size, png) in pngs)
            {
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);        // palette
                writer.Write((byte)0);        // reserved
                writer.Write((short)1);       // planes
                writer.Write((short)32);      // bits per pixel
                writer.Write(png.Length);
                writer.Write(offset);
                offset += png.Length;
            }
            foreach (var (_, png) in pngs)
            {
                writer.Write(png);
            }
        }
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }

    private static byte[] RenderPng(ImageSource drawing, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(drawing, new Rect(0, 0, size, size));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } };
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
