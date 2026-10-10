namespace Emu86;

internal sealed class KeyboardController
{
    public readonly Queue<byte> Output = new();
    public byte Last;
    public byte CommandByte = 0x45;
    public int PendingCommand;

    // AUX ポートの PS/2 マウス。マウス由来のバイトは Mouse.Output に積み、ステータス bit5 で区別する
    // (実機の 8042 は 1 つの出力バッファを使うが、キーボードとマウスのバイトが同時に待つことはまれなので
    // キーボード側を先に返す 2 本のキューで近似する)。
    public readonly MouseDevice Mouse = new();

    // 出力ポート(0xD1)または 0xFE パルスでシステムリセットが要求された(ランナーが確認して再起動する)。
    public bool ResetRequested;

    // ホスト(表示ウィンドウ等、別スレッド)から注入されるスキャンコード(セット 1、E0 プレフィックス込み)。
    // 実行スレッドが Pump で 1 バイトずつ出力バッファへ移す。スナップショットには含めない。
    public readonly System.Collections.Concurrent.ConcurrentQueue<byte> Injected = new();

    public void Inject(params byte[] scancodes)
    {
        foreach (var scancode in scancodes) Injected.Enqueue(scancode);
    }

    bool AuxDisabled => (CommandByte & 0x20) != 0;

    // 出力バッファが空のときだけ次のスキャンコード(またはマウスパケット)を移す(実機のキーボードも
    // 8042 の OBF が空くまで次のバイトを送らない)。移したら true。
    public bool Pump()
    {
        if (Output.Count > 0 || Mouse.Output.Count > 0) return false;
        if (Injected.TryDequeue(out var scancode))
        {
            Output.Enqueue(scancode);
            return true;
        }
        return !AuxDisabled && Mouse.Pump();
    }

    // IRQ1 を上げるべき状態: コマンドバイトでキーボード割り込みが許可され、出力バッファにキーボードのデータがある。
    public bool IrqAsserted => (CommandByte & 0x01) != 0 && Output.Count > 0;

    // IRQ12 を上げるべき状態: AUX 割り込みが許可され、キーボードのデータが無くマウスのデータがある。
    public bool AuxIrqAsserted => (CommandByte & 0x02) != 0 && Output.Count == 0 && Mouse.Output.Count > 0;

    public byte Read(int port)
    {
        switch (port)
        {
            case 0x60: // 8042 出力バッファ(読むと OBF が落ちる)
                if (Output.Count > 0) Last = Output.Dequeue();
                else if (Mouse.Output.Count > 0) Last = Mouse.Output.Dequeue();
                return Last;
            case 0x64: // 8042 ステータス: bit0=OBF, bit1=IBF(常に0), bit2=システムフラグ, bit4=キーボード非禁止, bit5=AUX データ
                return (byte)(0x14 | (Output.Count > 0 || Mouse.Output.Count > 0 ? 1 : 0) | (Output.Count == 0 && Mouse.Output.Count > 0 ? 0x20 : 0));
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
                    case 0xA7: CommandByte |= 0x20; break;                    // AUX 禁止
                    case 0xA8: CommandByte &= 0xDF; break;                    // AUX 許可
                    case 0xAA: Output.Enqueue(0x55); break;                  // 自己診断 OK
                    case 0xAB: Output.Enqueue(0x00); break;                  // インタフェース試験 OK
                    case 0xA9: Output.Enqueue(0x00); break;                  // マウスポート試験 OK
                    case 0xD0: Output.Enqueue(0x03); break;                  // 出力ポート読み(A20 有効・リセット非活性)
                    case 0xD1 or 0xD2 or 0xD3 or 0xD4: PendingCommand = val; break; // 次の 0x60 書き込みが引数
                    case >= 0xF0: if ((val & 1) == 0) ResetRequested = true; break; // 出力ポートのパルス: bit0=リセット
                    default: break;                                              // AD/AE(禁止/許可)、A7/A8 等は無視
                }
                break;
            case 0x60: // 8042 データ: 保留コマンドの引数、またはキーボードへのコマンド
                switch (PendingCommand)
                {
                    case 0x60: CommandByte = val; break;
                    case 0xD1: if ((val & 1) == 0) ResetRequested = true; break; // 出力ポート書き込み: bit0=0 でリセット
                    case 0xD2: Output.Enqueue(val); break; // 出力バッファへ書き込み
                    case 0xD3: Mouse.Output.Enqueue(val); break; // AUX 出力バッファへ書き込み
                    case 0xD4: Mouse.Command(val); break;       // マウスへ送る
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
