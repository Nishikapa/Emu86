using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.States;
using static Emu86.StringOps;
using static Emu86.CpuStates;
using static Emu86.Fetch;
using static Emu86.Decoder;

namespace Emu86;

// 文字列命令と REP プレフィックス: 1 要素分は StringOps、ここでは命令としての繰り返しと ZF 終了条件。
static partial class Instructions
{
    // 文字列命令(1 要素分は StringOps)。w ビットで要素幅を決める。
    static State<Unit> StringInstruction(string name, Func<bool, State<Unit>> element) =>
        from _1 in SetLog(name)
        from w in WidthBit
        from _2 in element(w)
        select unit;

    static State<Unit> Movs_A4_A5 => StringInstruction("Movs_A4_A5", Movs);

    static State<Unit> Stos_AA_AB => StringInstruction("Stos_AA_AB", Stos);

    static State<Unit> Lods_AC_AD => StringInstruction("Lods_AC_AD", Lods);

    static State<Unit> Cmps_A6_A7 => StringInstruction("Cmps_A6_A7", Cmps);

    static State<Unit> Scas_AE_AF => StringInstruction("Scas_AE_AF", Scas);

    static State<Unit> Ins_6C_6D => StringInstruction("Ins_6C_6D", Ins);

    static State<Unit> Outs_6E_6F => StringInstruction("Outs_6E_6F", Outs);

    // REP が前置できる文字列命令のオペコード集合。
    static readonly HashSet<int> stringOps = [0x6C, 0x6D, 0x6E, 0x6F, 0xA4, 0xA5, 0xA6, 0xA7, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF];

    // ZF を見て継続判定する比較系（CMPS/SCAS）。それ以外は CX のみで判定する。
    static readonly HashSet<int> zfStringOps = [0xA6, 0xA7, 0xAE, 0xAF];

    // 文字列命令を CX 回繰り返す共通ループ。
    //   repZf==null     : REP    (0xF3, 非比較系) — CX 回そのまま繰り返す
    //   repZf==true     : REPE   (0xF3, 比較系)   — ZF==0 になったら打ち切り
    //   repZf==false    : REPNE  (0xF2)           — ZF==1 になったら打ち切り
    static State<Unit> RepLoop(bool? repZf) => (env, cpu1, ope) =>
    {
        // REP と文字列命令の間に来るプレフィックス(66/67/セグメント/F0)を消費し、
        // 対応するデコード文脈を立てる。例: F3 66 AB = REP STOSW(16ビット幅)。
        var (_, prefixes, prefixLength) = Prefixes(CodeSource.At(env, cpu1), 0);
        cpu1.Decode.Prefixes |= prefixes;
        cpu1.eip += (uint)prefixLength;

        var (ok, op, cpu2, log) = GetMemoryDataIp8(env, cpu1, ope);
        if (!ok)
            return (false, default, cpu1, log);

        // F3 90 = PAUSE(スピンループヒント)。実質 NOP として両バイトを消費する。
        if (op == 0x90)
            return (true, unit, cpu2, log);

        // F3/F2 0F xx: TZCNT/LZCNT 等の新命令エンコーディング。この CPU 世代では
        // REP プレフィックスは無視され、素の 2 バイト命令(BSF/BSR 等)として実行される
        // (Linux の __ffs は互換性のため意図的に "rep; bsf" を使う)。
        if (op == 0x0F)
        {
            var (okB, op2, cpuB, logB) = GetMemoryDataIp8(env, cpu2, ope);
            if (!okB || !twoBytes[0x0F].TryGetValue(op2, out var twoByte))
                return (false, default, cpu1, log);
            return twoByte(env, cpuB, [0x0F, op2]);
        }

        if (!oneByte.TryGetValue(op, out var state))
            return (false, default, cpu1, log);

        // 文字列命令以外に付いた REP は無視して、その命令をそのまま実行する(gcc/glibc の `rep ret` など。実 CPU も無視する)。
        if (!stringOps.Contains(op))
            return state(env, cpu2, [op]);

        // 比較系のみ ZF 条件を適用する（MOVS/STOS/LODS は CX だけで回す）。
        var checkZf = repZf is bool && zfStringOps.Contains(op);
        // 32ビットアドレスサイズでは ECX、そうでなければ CX でカウントする。
        var a32 = cpu2.code32 != cpu2.address_size_prefix;
        var stop = false;
        // 1 反復: 文字列命令を 1 要素実行 → カウンタを減らす → 完了済みの反復として記録し、比較系なら ZF で打ち切りを決める。
        // 反復は数百万回になる(rep insw のディスク読み込み)ので、各 State は 1 度だけ組み立てて Then で連ねる。
        State<Unit> instruction = (e, c, _) => state(e, c, [op]);
        var decrement = a32 ? _ecx.Modify(ecx => ecx - 1) : _cx.Modify(cx => (ushort)(cx - 1));
        var commit = SetCpu(c => { c.Decode.CommitRepeat(c); stop = checkZf && c.zf != repZf.Value; return c; });
        return While(c => !stop && (a32 ? c.ecx : c.cx) != 0, instruction.Then(decrement).Then(commit))(env, cpu2, ope);
    };

    // REP / REPE / REPZ (0xF3)
    static State<Unit> Rep_F3 => RepLoop(true);

    // REPNE / REPNZ (0xF2)
    static State<Unit> Repne_F2 => RepLoop(false);
}
