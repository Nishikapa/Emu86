using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Emu86;

// VGA の表示を画像にする(表示ウィンドウと同じ描画)。--screen で停止時と定期保存のたびに PNG へ書く。
public static class ScreenCapture
{
    public static Bitmap ToBitmap(VgaDevice vga)
    {
        var (width, height) = vga.Resolution();
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;
        var pixels = new uint[width * height];
        (width, height) = vga.Render(pixels);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(MemoryMarshal.Cast<uint, int>(pixels.AsSpan(y * width, width)).ToArray(), 0, data.Scan0 + y * data.Stride, width);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    public static void Save(VgaDevice vga, string path)
    {
        using var bitmap = ToBitmap(vga);
        if (bitmap == null) return;
        var temporary = path + ".tmp";
        bitmap.Save(temporary, ImageFormat.Png);
        File.Move(temporary, path, overwrite: true);
    }
}
