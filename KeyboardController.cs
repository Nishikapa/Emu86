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

    // コントローラコマンド(ポート 0x64)の表。引数を取るもの(0x60、0xD1-0xD4)は PendingCommand に記録し、次の 0x60 書き込みを
    // Arguments の表で処理する。0xF0-0xFF は出力ポートのパルス(bit0=0 でリセット)。表にないコマンド(AD/AE の禁止/許可など)は無視。
    static readonly Dictionary<byte, Action<KeyboardController>> ControllerCommands = new()
    {
        [0x20] = kc => kc.Output.Enqueue(kc.CommandByte), // コマンドバイト読み出し
        [0x60] = kc => kc.PendingCommand = 0x60,          // 次の 0x60 書き込みがコマンドバイト
        [0xA7] = kc => kc.CommandByte |= 0x20,            // AUX 禁止
        [0xA8] = kc => kc.CommandByte &= 0xDF,            // AUX 許可
        [0xA9] = kc => kc.Output.Enqueue(0x00),           // マウスポート試験 OK
        [0xAA] = kc => kc.Output.Enqueue(0x55),           // 自己診断 OK
        [0xAB] = kc => kc.Output.Enqueue(0x00),           // インタフェース試験 OK
        [0xD0] = kc => kc.Output.Enqueue(0x03),           // 出力ポート読み(A20 有効・リセット非活性)
        [0xD1] = kc => kc.PendingCommand = 0xD1,          // 出力ポート書き込み(引数)
        [0xD2] = kc => kc.PendingCommand = 0xD2,          // 出力バッファへ書き込み(引数)
        [0xD3] = kc => kc.PendingCommand = 0xD3,          // AUX 出力バッファへ書き込み(引数)
        [0xD4] = kc => kc.PendingCommand = 0xD4,          // マウスへ送る(引数)
    };

    static readonly Dictionary<int, Action<KeyboardController, byte>> Arguments = new()
    {
        [0x60] = (kc, value) => kc.CommandByte = value,
        [0xD1] = (kc, value) => { if ((value & 1) == 0) kc.ResetRequested = true; }, // 出力ポート: bit0=0 でリセット
        [0xD2] = (kc, value) => kc.Output.Enqueue(value),
        [0xD3] = (kc, value) => kc.Mouse.Output.Enqueue(value),
        [0xD4] = (kc, value) => kc.Mouse.Command(value),
    };

    // キーボードへのコマンド(ポート 0x60、保留コマンドが無いとき)の応答: ACK に続く追加応答。表にないコマンドは ACK だけ。
    static readonly Dictionary<byte, byte[]> KeyboardReplies = new()
    {
        [0xFF] = [0xAA],       // リセット → BAT OK
        [0xF2] = [0xAB, 0x83], // ID
    };

    public void Write(int port, byte val)
    {
        switch (port)
        {
            case 0x64:
                PendingCommand = 0;
                if (val >= 0xF0) { if ((val & 1) == 0) ResetRequested = true; }
                else if (ControllerCommands.TryGetValue(val, out var command)) command(this);
                break;
            case 0x60:
                if (Arguments.TryGetValue(PendingCommand, out var argument)) argument(this, val);
                else
                {
                    Output.Enqueue(0xFA);
                    if (KeyboardReplies.TryGetValue(val, out var replies))
                        foreach (var reply in replies) Output.Enqueue(reply);
                }
                PendingCommand = 0;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(port));
        }
    }
}
