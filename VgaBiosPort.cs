namespace Emu86;

// VGA BIOS スタブ(VgaBiosStub)のサービスの呼び出し口。ROM の INT 10h ハンドラ(実コード)がレジスタをポートへ送り、
// 結果のレジスタと、ES:DI へ返すバイト列を読み戻す。ハンドラを実行するのが CPU そのもの(リアルモード、V86 モード)でも、
// OS が持つ x86 エミュレータ(Windows 7 の HAL は ROM のコードを解釈して INT 10h を呼ぶ)でも、ポートの入出力はここへ届く。
//   0x1D0(語の書き込み)  : 呼び出しの開始。AX。
//   0x1D2(語の書き込み)  : 引数の続き。BX、CX、DX、ES:DI の先頭 4 バイト(下位の語、上位の語)。
//   0x1D2(語の読み出し)  : 結果。最初の読み出しでサービスを実行し、AX、BX、CX、DX、ES:DI へ書くバイト数の順に返す。
//   0x1D4(バイトの読み出し): ES:DI へ書くバイト列を先頭から 1 バイトずつ。
// 呼び出しの途中でスナップショットを取っても続きから再開できるよう、状態はスナップショットに含める。
internal sealed class VgaBiosPort
{
    public const int CallPort = 0x1D0, WordPort = 0x1D2, DataPort = 0x1D4;
    const int ArgumentCount = 6, ResultCount = 5;
    // ES:DI へ返すバイト列の上限(VBE のコントローラ情報の 512 バイトが最大)。
    const int MaxReply = 512;

    readonly ushort[] arguments = new ushort[ArgumentCount];    // AX、BX、CX、DX、ES:DI の先頭(2 語)
    readonly ushort[] results = new ushort[ResultCount];        // AX、BX、CX、DX、バイト数
    int received;           // 受け取った引数の数
    int returned = -1;      // 返した結果の数(-1 はサービスの実行前)
    byte[] reply = [];      // ES:DI へ返すバイト列
    int position;           // 次に返すバイト

    public static readonly Codec<VgaBiosPort> Layout = new Codec<VgaBiosPort>()
        .U16s(d => d.arguments)
        .U16s(d => d.results)
        .I32(d => d.received, (d, v) => d.received = v)
        .I32(d => d.returned, (d, v) => d.returned = v)
        .ByteArray(d => d.reply, (d, v) => d.reply = v, MaxReply)
        .I32(d => d.position, (d, v) => d.position = v)
        .Check(d => d.received is >= 0 and <= ArgumentCount && d.returned is >= -1 and <= ResultCount && d.position >= 0 && d.position <= d.reply.Length,
            "Invalid VGA BIOS call state");

    // サービスから見た呼び出し: 呼び出し側が ES:DI に置いていた先頭 4 バイトと、ES:DI へ返すバイト列。
    public uint Head => (uint)(arguments[4] | arguments[5] << 16);

    public void Reply(byte[] data) => (reply, position) = (data, 0);

    public void Begin(ushort ax)
    {
        Array.Clear(arguments);
        arguments[0] = ax;
        (received, returned) = (1, -1);
        Reply([]);
    }

    // Begin の後の引数(Begin の前や、6 個を超えた分は捨てる)。
    public void Argument(ushort value)
    {
        if (received is > 0 and < ArgumentCount) arguments[received++] = value;
    }

    // 呼び出しが始まっていないとき(ポートを読んだだけ)と、結果を返し終えた後は 0xFFFF(何も実行しない)。
    public ushort Result(EmuEnvironment env)
    {
        if (received == 0) return 0xFFFF;
        if (returned < 0)
        {
            var registers = new CPU { eax = arguments[0], ebx = arguments[1], ecx = arguments[2], edx = arguments[3] };
            VgaBiosStub.Service(env, registers);
            (results[0], results[1], results[2], results[3], results[4]) = (registers.ax, registers.bx, registers.cx, registers.dx, (ushort)reply.Length);
            returned = 0;
        }
        return returned < ResultCount ? results[returned++] : (ushort)0xFFFF;
    }

    public byte Data() => position < reply.Length ? reply[position++] : (byte)0xFF;
}
