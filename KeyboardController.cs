namespace Emu86;

internal sealed class KeyboardController
{
    public readonly Queue<byte> Output = new();
    public byte Last;
    public byte CommandByte = 0x45;
    public int PendingCommand;

    public byte Read(int port)
    {
        switch (port)
        {
            case 0x60: // 8042 出力バッファ(読むと OBF が落ちる)
                if (Output.Count > 0) Last = Output.Dequeue();
                return Last;
            case 0x64: // 8042 ステータス: bit0=OBF, bit1=IBF(常に0), bit2=システムフラグ, bit4=キーボード非禁止
                return (byte)(0x14 | (Output.Count > 0 ? 1 : 0));
            default: throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    public void Write(int port, byte val)
    {
        switch (port)
        {
            case 0x64: // 8042 コントローラコマンド
                PendingCommand = 0;
                switch (val)
                {
                    case 0x20: Output.Enqueue(CommandByte); break;       // コマンドバイト読み出し
                    case 0x60: PendingCommand = 0x60; break;                  // 次の 0x60 書き込みがコマンドバイト
                    case 0xAA: Output.Enqueue(0x55); break;                  // 自己診断 OK
                    case 0xAB: Output.Enqueue(0x00); break;                  // インタフェース試験 OK
                    case 0xA9: Output.Enqueue(0x00); break;                  // マウスポート試験 OK
                    case 0xD0: Output.Enqueue(0x03); break;                  // 出力ポート読み(A20 有効・リセット非活性)
                    case 0xD1 or 0xD2 or 0xD3 or 0xD4: PendingCommand = val; break; // 次の 0x60 書き込みが引数
                    default: break;                                              // AD/AE(禁止/許可)、A7/A8、FE(リセット)等は無視
                }
                break;
            case 0x60: // 8042 データ: 保留コマンドの引数、またはキーボードへのコマンド
                switch (PendingCommand)
                {
                    case 0x60: CommandByte = val; break;
                    case 0xD1: break; // 出力ポート書き込み(A20 等)は無視
                    case 0xD2: Output.Enqueue(val); break; // 出力バッファへ書き込み
                    case 0xD3 or 0xD4: break;                   // マウス関連は未接続
                    default:
                        // キーボードコマンド: ACK を返し、必要な追加応答を積む。
                        Output.Enqueue(0xFA);
                        if (val == 0xFF) Output.Enqueue(0xAA);                          // リセット → BAT OK
                        else if (val == 0xF2) { Output.Enqueue(0xAB); Output.Enqueue(0x83); } // ID
                        break;
                }
                PendingCommand = 0;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(port));
        }
    }
}
