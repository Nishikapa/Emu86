using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Emu86;

// VGA 出力を表示する WinForms ウィンドウ。クライアント領域は VGA サイズ(640×480)で開き、
// 現在のビデオモードの解像度(テキスト 640×400、mode 12h 640×480、mode 13h 320×200 など)を
// 最近傍補間で引き伸ばして描く。キー入力は WM_KEYDOWN/UP のハードウェアスキャンコード
// (セット 1)をそのまま 8042 へ注入する。描画は約 30fps のタイマで VgaDevice.Dirty を見て更新する。
internal sealed class VgaWindow : Form
{
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    readonly EmuEnvironment env;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    Bitmap frame;
    uint[] pixels = [];
    string status = "running";

    public VgaWindow(EmuEnvironment env)
    {
        this.env = env;
        Text = "Emu86";
        ClientSize = new Size(640, 480);
        MinimumSize = new Size(320, 240);
        BackColor = Color.Black;
        DoubleBuffered = true;
        ImeMode = ImeMode.Disable;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        env.Vga.FallbackFont ??= BuildFallbackFont();
        timer.Tick += (_, _) => Refresh(force: false);
        timer.Start();
        Refresh(force: true);
    }

    // 実行スレッドから: 実行が終わった(上限到達・停止)ことをタイトルに反映する。
    public void EmulationStopped(string reason)
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => { status = reason; Refresh(force: true); }); }
        catch (InvalidOperationException) { }
    }

    void Refresh(bool force)
    {
        var vga = env.Vga;
        if (!force && !vga.Dirty) return;
        var (width, height) = vga.Resolution();
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return;
        if (pixels.Length < width * height) pixels = new uint[width * height];
        (width, height) = vga.Render(pixels);
        if (frame == null || frame.Width != width || frame.Height != height)
        {
            frame?.Dispose();
            frame = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        }
        var data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(MemoryMarshal.Cast<uint, int>(pixels.AsSpan(y * width, width)).ToArray(), 0, data.Scan0 + y * data.Stride, width);
        }
        finally { frame.UnlockBits(data); }
        Text = $"Emu86 - {vga.ModeDescription()} - {status}";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);
        if (frame == null) return;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(frame, ClientRectangle);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    // モニタ間で DPI が変わっても物理ピクセル数(既定 640×480)を保つ。基底クラスは Windows が提案する
    // 拡大縮小後の矩形を適用するので、その後で元のクライアントサイズへ戻す。
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        var client = ClientSize;
        base.OnDpiChanged(e);
        ClientSize = client;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
        {
            var lParam = (long)m.LParam;
            var scancode = (byte)((lParam >> 16) & 0xFF);
            var extended = ((lParam >> 24) & 1) != 0;
            var release = m.Msg is WM_KEYUP or WM_SYSKEYUP;
            if (scancode != 0)
            {
                var code = (byte)(scancode | (release ? 0x80 : 0));
                if (extended) env.Keyboard.Inject(0xE0, code);
                else env.Keyboard.Inject(code);
            }
            // F10 や Alt 系がメニューとして解釈されないよう、キー入力はここで消費する。
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessDialogKey(Keys keyData) => true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            frame?.Dispose();
        }
        base.Dispose(disposing);
    }

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
}
