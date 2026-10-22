using System.Collections.Concurrent;

namespace Emu86;

// PS/2 マウス(8042 の AUX ポート側のデバイス)。標準 3 バイトパケットと、サンプルレート 200/100/80 の
// 魔法シーケンスで有効になる IntelliMouse(ID 3、ホイール付き 4 バイトパケット)に対応する。
// ホスト(表示ウィンドウ)は Move で相対移動とボタン状態を積み、実行スレッドが Pump で 1 パケットずつ
// 出力バッファへ移す。出力バッファの内容は 8042 がポート 0x60 で返し、IRQ12 の根拠になる。
public sealed class MouseDevice
{
    public readonly Queue<byte> Output = new();
    public bool Enabled;          // ストリームモードで報告中(F4)
    public bool Remote;           // リモートモード(F0): EB で読む
    public byte SampleRate = 100;
    public byte Resolution = 2;
    public bool Scaling2;
    public byte Id;               // 0=標準、3=IntelliMouse
    public int PendingCommand;    // F3/E8 の引数待ち
    public byte LastButtons;
    readonly byte[] rateHistory = new byte[3];

    readonly ConcurrentQueue<(int dx, int dy, int dz, byte buttons)> motion = new();

    // ホストから: PS/2 の向き(dy は上が正)で相対移動を積む。buttons: bit0=左 bit1=右 bit2=中。
    public void Move(int dx, int dy, int dz, byte buttons) => motion.Enqueue((dx, dy, dz, buttons));

    public bool HasPendingMotion => !motion.IsEmpty;

    // 出力バッファが空で報告中なら次のパケットを組み立てる。移したら true。
    public bool Pump()
    {
        if (Output.Count > 0 || !Enabled || Remote || !motion.TryDequeue(out var m)) return false;
        EnqueuePacket(m.dx, m.dy, m.dz, m.buttons);
        return true;
    }

    void EnqueuePacket(int dx, int dy, int dz, byte buttons)
    {
        LastButtons = (byte)(buttons & 7);
        var cdx = Math.Clamp(dx, -256, 255);
        var cdy = Math.Clamp(dy, -256, 255);
        var head = 0x08 | LastButtons | (cdx < 0 ? 0x10 : 0) | (cdy < 0 ? 0x20 : 0) | (dx != cdx ? 0x40 : 0) | (dy != cdy ? 0x80 : 0);
        Output.Enqueue((byte)head);
        Output.Enqueue((byte)cdx);
        Output.Enqueue((byte)cdy);
        if (Id == 3) Output.Enqueue((byte)Math.Clamp(dz, -8, 7));
    }

    // コマンド表: 応答(ACK から始まるバイト列)と動作。F3/E8 は次のバイトを引数に取り(PendingCommand)、Arguments で処理する。
    // 表にないコマンドは再送要求(FE)。
    readonly record struct MouseCommand(byte[] Replies, Action<MouseDevice> Action = null, bool TakesArgument = false);

    static readonly Dictionary<byte, MouseCommand> Commands = new()
    {
        [0xFF] = new([0xFA, 0xAA, 0x00], mouse => // リセット: ACK, BAT 完了, ID
        {
            mouse.Enabled = false; mouse.Remote = false; mouse.SampleRate = 100; mouse.Resolution = 2; mouse.Scaling2 = false; mouse.Id = 0;
            Array.Clear(mouse.rateHistory);
        }),
        [0xF6] = new([0xFA], mouse => { mouse.Enabled = false; mouse.SampleRate = 100; mouse.Resolution = 2; mouse.Scaling2 = false; }), // 既定値
        [0xF5] = new([0xFA], mouse => mouse.Enabled = false),
        [0xF4] = new([0xFA], mouse => mouse.Enabled = true),
        [0xF3] = new([0xFA], TakesArgument: true),              // サンプルレート
        [0xE8] = new([0xFA], TakesArgument: true),              // 解像度
        [0xF2] = new([0xFA], mouse => mouse.Output.Enqueue(mouse.Id)),
        [0xF0] = new([0xFA], mouse => mouse.Remote = true),
        [0xEA] = new([0xFA], mouse => mouse.Remote = false),
        [0xEB] = new([0xFA], mouse => mouse.ReportOnce()),      // データ読み出し(リモートモード)
        [0xE9] = new([0xFA], mouse =>                           // ステータス要求
        {
            mouse.Output.Enqueue((byte)((mouse.Remote ? 0x40 : 0) | (mouse.Enabled ? 0x20 : 0) | (mouse.Scaling2 ? 0x10 : 0) | mouse.LastButtons));
            mouse.Output.Enqueue(mouse.Resolution);
            mouse.Output.Enqueue(mouse.SampleRate);
        }),
        [0xE7] = new([0xFA], mouse => mouse.Scaling2 = true),
        [0xE6] = new([0xFA], mouse => mouse.Scaling2 = false),
        [0xEE] = new([0xFA]), // エコーモード(簡略)
        [0xEC] = new([0xFA]),
    };

    static readonly Dictionary<int, Action<MouseDevice, byte>> Arguments = new()
    {
        [0xF3] = (mouse, value) =>
        {
            mouse.SampleRate = value;
            mouse.rateHistory[0] = mouse.rateHistory[1]; mouse.rateHistory[1] = mouse.rateHistory[2]; mouse.rateHistory[2] = value;
            if (mouse.rateHistory[0] == 200 && mouse.rateHistory[1] == 100 && mouse.rateHistory[2] == 80) mouse.Id = 3; // IntelliMouse の魔法シーケンス
        },
        [0xE8] = (mouse, value) => mouse.Resolution = (byte)(value & 3),
    };

    void ReportOnce()
    {
        if (motion.TryDequeue(out var m)) EnqueuePacket(m.dx, m.dy, m.dz, m.buttons);
        else EnqueuePacket(0, 0, 0, LastButtons);
    }

    // 8042 の D4 経由でマウスへ送られた 1 バイト。応答(ACK 等)を出力バッファへ積む。
    public void Command(byte value)
    {
        if (PendingCommand != 0)
        {
            var command = PendingCommand;
            PendingCommand = 0;
            Output.Enqueue(0xFA);
            if (Arguments.TryGetValue(command, out var argument)) argument(this, value);
            return;
        }
        if (!Commands.TryGetValue(value, out var entry))
        {
            Output.Enqueue(0xFE);
            return;
        }
        foreach (var reply in entry.Replies) Output.Enqueue(reply);
        entry.Action?.Invoke(this);
        if (entry.TakesArgument) PendingCommand = value;
    }

    // スナップショット(v9 から)。
    static readonly Codec<MouseDevice> Layout = new Codec<MouseDevice>()
        .ByteQueue(m => m.Output, 1 << 16)
        .Bool(m => m.Enabled, (m, v) => m.Enabled = v)
        .Bool(m => m.Remote, (m, v) => m.Remote = v)
        .U8(m => m.SampleRate, (m, v) => m.SampleRate = v)
        .U8(m => m.Resolution, (m, v) => m.Resolution = v)
        .Bool(m => m.Scaling2, (m, v) => m.Scaling2 = v)
        .U8(m => m.Id, (m, v) => m.Id = v)
        .I32(m => m.PendingCommand, (m, v) => m.PendingCommand = v)
        .U8(m => m.LastButtons, (m, v) => m.LastButtons = v)
        .Bytes(m => m.rateHistory);

    public void SaveState(BinaryWriter writer) => Layout.Save(writer, this);

    public void LoadState(BinaryReader reader) => Layout.Load(reader, this, SnapshotStore.Version);
}
