using static Emu86.Ext;
using static Emu86.Interrupts;

namespace Emu86;

// 8259 経由のハードウェア割り込み配送。
//   Select  : 配送できる割り込み線を優先順に 1 本選ぶ(毎命令呼ばれるので、デリゲートを使わず割り当てもしない)。
//   Deliver : 選ばれた線をベクタへ配送し、PIC の in-service を立て、源の要求を取り下げる State。
//             InstructionExecutor を通して実行し、配送中のページフォルトでは CPU を配送前へ戻す。
// 線番号は 0-7 がマスタ、8-15 がスレーブ(IRQ12 = スレーブ入力 4、IRQ14 = スレーブ入力 6)。
internal static class IrqDelivery
{
    // 配送の優先順。実行ループが個別に判定していた順(タイマ、キーボード、COM1、COM2、マウス、ATA)を保つ。
    // 8259 本来の優先度(0, 1, 8-15, 3-7)とは COM とスレーブの前後が異なるが、動作を変えないために維持する。
    static readonly int[] Priority = [0, 1, 4, 3, 12, 14];

    // 線ごとの配送 State(起動時に 1 度だけ組み立てる)。
    static readonly State<Unit>[] Delivery = [.. Enumerable.Range(0, 16).Select(Deliver)];

    public static string Name(int line) => line switch
    {
        0 => "timer",
        1 => "keyboard",
        3 or 4 => "serial",
        12 => "mouse",
        14 => "ATA",
        _ => $"IRQ{line}",
    };

    // 割り込み線が要求を上げているか(デバイス側の状態)。
    // ATA はプロテクトモードでのみ配送する(リアルモードの SeaBIOS はポーリングで使う)。
    static bool Asserted(EmuEnvironment env, CPU cpu, int line) => line switch
    {
        0 => env.TimerIrqPending,
        1 => env.Keyboard.IrqAsserted,
        3 => env.Com2.IrqAsserted,
        4 => env.Com1.IrqAsserted,
        12 => env.Keyboard.AuxIrqAsserted,
        14 => cpu.pe && env.Ata is { IrqPending: true },
        _ => false,
    };

    public static int Vector(EmuEnvironment env, int line) =>
        line < 8 ? env.PicMasterBase + line : env.PicSlaveBase + (line - 8);

    // PIC でマスクされておらず、前回の配送が in-service(EOI 前)でもない。
    // スレーブの線はカスケード(マスタ入力 2)のマスクも見る。in-service は実機の 8259 と同じく EOI で降りる。
    // これがないとハンドラ完了前に次を注入して多重ネストし、スタックを押し下げて割り込みストームになる。
    static bool Unmasked(EmuEnvironment env, int line) => line < 8
        ? (env.PicMasterMask & (1 << line)) == 0 && (env.PicMasterIsr & (1 << line)) == 0
        : (env.PicSlaveMask & (1 << (line - 8))) == 0 && (env.PicMasterMask & 0x04) == 0
            && (env.PicSlaveIsr & (1 << (line - 8))) == 0;

    // CPU が受け付けられるか: IF=1 で、プロテクトモードでは OS が PIC を再プログラム(base>=0x20)済みで
    // IDT にゲートが用意されていること(リアルモードは IVT 経由で常に配送できる)。
    static bool Acceptable(EmuEnvironment env, CPU cpu, int vector) =>
        cpu.jf && (!cpu.pe || (env.PicMasterBase >= 0x20 && cpu.idt_limit >= (vector + 1) * 8 - 1));

    // 配送できる最初の線。無ければ -1。
    public static int Select(EmuEnvironment env, CPU cpu)
    {
        if (!cpu.jf) return -1;
        foreach (var line in Priority)
            if (Asserted(env, cpu, line) && Unmasked(env, line) && Acceptable(env, cpu, Vector(env, line)))
                return line;
        return -1;
    }

    public static State<Unit> Pending(int line) => Delivery[line];

    static State<Unit> Deliver(int line) =>
        from vector in GetDataFromEnvCpu((env, cpu) => Vector(env, line))
        from _1 in Interrupt(vector)
        from _2 in SetCpu((env, cpu) => { Acknowledge(env, line); return cpu; })
        select Unit.unit;

    // 配送後: in-service を立て(スレーブ経由はカスケードも)、レベル要求でない源は取り下げる。
    static void Acknowledge(EmuEnvironment env, int line)
    {
        if (line < 8)
            env.PicMasterIsr |= (byte)(1 << line);
        else
        {
            env.PicSlaveIsr |= (byte)(1 << (line - 8));
            env.PicMasterIsr |= 0x04;
        }
        switch (line)
        {
            case 0: env.TimerIrqPending = false; break;
            case 14: env.Ata.IrqPending = false; break;
        }
    }
}
