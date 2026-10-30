using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Emu86;

// VGA の表示を画像にする(表示ウィンドウと同じ描画)。--screen で停止時と定期保存のたびに PNG へ書く。
public static class ScreenCapture
{
    // VGA BIOS が無くフォントが VRAM に無いときのための 8×16 フォント(ウィンドウと画像保存で共有。1 度だけ作る)。
    static readonly Lazy<byte[]> fallbackFont = new(BuildFallbackFont);
    public static byte[] FallbackFont => fallbackFont.Value;

    // VGA BIOS が無くフォントが VRAM に無いときのための 8×16 フォント。
    // CP437 の 256 文字を等幅フォントで描き、2 値化してビットパターンにする。
    static byte[] BuildFallbackFont()
    {
        var font8x16 = new byte[256 * 16];
        Encoding cp437;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            cp437 = Encoding.GetEncoding(437);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            cp437 = Encoding.Latin1;
        }
        using var bitmap = new Bitmap(8, 16, PixelFormat.Format32bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        using var font = new Font("Consolas", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        for (var code = 0; code < 256; code++)
        {
            // 制御コードは CP437 の図形(スマイル等)に対応するが、ここでは空白にする。
            var text = code is < 0x20 or 0x7F ? " " : cp437.GetString([(byte)code]);
            graphics.Clear(Color.Black);
            graphics.DrawString(text, font, Brushes.White, -0.5f, 0f, format);
            for (var y = 0; y < 16; y++)
            {
                var row = 0;
                for (var x = 0; x < 8; x++)
                    if (bitmap.GetPixel(x, y).R > 96) row |= 0x80 >> x;
                font8x16[code * 16 + y] = (byte)row;
            }
        }
        return font8x16;
    }

    public static Bitmap ToBitmap(VgaDevice vga)
    {
        vga.FallbackFont ??= FallbackFont;
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
