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

    // 8042 の D4 経由でマウスへ送られた 1 バイト。応答(ACK 等)を出力バッファへ積む。
    public void Command(byte value)
    {
        if (PendingCommand != 0)
        {
            var command = PendingCommand;
            PendingCommand = 0;
            Output.Enqueue(0xFA);
            if (command == 0xF3)
            {
                SampleRate = value;
                rateHistory[0] = rateHistory[1]; rateHistory[1] = rateHistory[2]; rateHistory[2] = value;
                if (rateHistory[0] == 200 && rateHistory[1] == 100 && rateHistory[2] == 80) Id = 3;
            }
            else if (command == 0xE8)
                Resolution = (byte)(value & 3);
            return;
        }
        switch (value)
        {
            case 0xFF: // リセット: ACK, BAT 完了, ID
                Output.Enqueue(0xFA); Output.Enqueue(0xAA); Output.Enqueue(0x00);
                Enabled = false; Remote = false; SampleRate = 100; Resolution = 2; Scaling2 = false; Id = 0;
                Array.Clear(rateHistory);
                break;
            case 0xF6: // 既定値
                Output.Enqueue(0xFA);
                Enabled = false; SampleRate = 100; Resolution = 2; Scaling2 = false;
                break;
            case 0xF5: Output.Enqueue(0xFA); Enabled = false; break;
            case 0xF4: Output.Enqueue(0xFA); Enabled = true; break;
            case 0xF3 or 0xE8: Output.Enqueue(0xFA); PendingCommand = value; break;
            case 0xF2: Output.Enqueue(0xFA); Output.Enqueue(Id); break;
            case 0xF0: Output.Enqueue(0xFA); Remote = true; break;
            case 0xEA: Output.Enqueue(0xFA); Remote = false; break;
            case 0xEB: // データ読み出し(リモートモード)
                Output.Enqueue(0xFA);
                if (motion.TryDequeue(out var m)) EnqueuePacket(m.dx, m.dy, m.dz, m.buttons);
                else EnqueuePacket(0, 0, 0, LastButtons);
                break;
            case 0xE9: // ステータス要求
                Output.Enqueue(0xFA);
                Output.Enqueue((byte)((Remote ? 0x40 : 0) | (Enabled ? 0x20 : 0) | (Scaling2 ? 0x10 : 0) | LastButtons));
                Output.Enqueue(Resolution);
                Output.Enqueue(SampleRate);
                break;
            case 0xE7: Output.Enqueue(0xFA); Scaling2 = true; break;
            case 0xE6: Output.Enqueue(0xFA); Scaling2 = false; break;
            case 0xEE: Output.Enqueue(0xFA); break; // エコーモード(簡略)
            case 0xEC: Output.Enqueue(0xFA); break;
            default: Output.Enqueue(0xFE); break;   // 未知のコマンドは再送要求
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(Output.Count);
        foreach (var value in Output) writer.Write(value);
        writer.Write(Enabled); writer.Write(Remote); writer.Write(SampleRate); writer.Write(Resolution);
        writer.Write(Scaling2); writer.Write(Id); writer.Write(PendingCommand); writer.Write(LastButtons);
        writer.Write(rateHistory);
    }

    public void LoadState(BinaryReader reader)
    {
        Output.Clear();
        var count = SnapshotStore.ReadCount(reader, 1 << 16);
        for (var index = 0; index < count; index++) Output.Enqueue(reader.ReadByte());
        Enabled = reader.ReadBoolean(); Remote = reader.ReadBoolean(); SampleRate = reader.ReadByte(); Resolution = reader.ReadByte();
        Scaling2 = reader.ReadBoolean(); Id = reader.ReadByte(); PendingCommand = reader.ReadInt32(); LastButtons = reader.ReadByte();
        reader.BaseStream.ReadExactly(rateHistory);
    }
}
