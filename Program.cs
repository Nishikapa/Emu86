using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.Operands;
using static Emu86.States;
using static Emu86.DataAccess;
using static Emu86.StringOps;
using static Emu86.Segments;
using static Emu86.Ports;
using static Emu86.BitOps;
using static Emu86.Interrupts;
using static Emu86.CpuStates;
using static Emu86.Fetch;
using static Emu86.Decoder;
using static Emu86.AluStates;

namespace Emu86;

static partial class Program
{
    /// 制御転送の部品 ///////////////////////////////
    // 命令ストリームの far ポインタ ptr16:16 / ptr16:32(offset が先、segment が後)。
    // 実効オペランドサイズが32ビット(66プレフィックスまたは32ビットコード)なら offset は 32 ビット。
    static State<(uint offset, ushort segment)> FarPointerIp(int type) =>
        from offset in type == 2 ? GetMemoryDataIp32 : GetMemoryDataIp16.Select(dw => (uint)dw)
        from segment in GetMemoryDataIp16
        select (offset, segment);

    // メモリの far ポインタ m16:16 / m16:32(offset の直後に selector)。
    static State<(uint offset, ushort segment)> FarPointerMem(MemAddr addr, int type) =>
        from offset in type == 2 ? GetMemOrRegData32(addr) : GetMemOrRegData16(addr).Select(dw => (uint)dw)
        from segment in GetMemOrRegData16((addr.isMem, addr.addr + (uint)(type == 2 ? 4 : 2)))
        select (offset, segment);

    // CS:EIP を target へ切り替える。CS のロードは LoadSReg 経由(プロテクトモードでは GDT 記述子から基底/D ビットを反映)。
    static State<Unit> FarTransfer((uint offset, ushort segment) target) =>
        from _1 in _eip.Set(target.offset)
        from _2 in LoadSReg(1, target.segment)
        select unit;

    // 戻り番地(現在の IP/EIP = 次命令)をオペランドサイズ幅で push する。
    static State<Unit> PushReturnNear(int type) =>
        from eip in _eip.Get()
        from _ in Push(eip.ToTypeData(type))
        select unit;

    // CS と戻り番地をオペランドサイズ幅で push する(far CALL のリターンフレーム)。
    static State<Unit> PushReturnFar(int type) =>
        from cs in GetSRegData(1)
        from _1 in Push(((uint)cs).ToTypeData(type))
        from _2 in PushReturnNear(type)
        select unit;

    // 近傍ジャンプ先の設定(type に応じて IP / EIP)。
    static State<Unit> JmpTo(Data target) =>
        2 == target.type ? _eip.Set(target.dd) : _ip.Set(target.dw);

    // 相対分岐 JMP rel8 (0xEB) / rel16・rel32 (0xE9)。
    static State<Unit> JumpRelative(string name, State<int> rel) =>
        from _1 in SetLog(name)
        from offset in rel
        from _2 in IpInc(offset)
        select unit;

    // 条件分岐 Jcc rel8 (70-7F) / rel16・rel32 (0F 80-8F)。条件はオペコード末尾バイトの下位 4 ビット。
    static State<Unit> ConditionalJump(string name, State<int> rel) =>
        from _1 in SetLog(name)
        from opecode in Opecodes
        from taken in Jcc(opecode[^1] & 0xF)
        from offset in rel
        from _2 in When(taken, IpInc(offset))
        select unit;

    static State<Unit> Jmp_EB => JumpRelative("Jmp_EB", Rel8);
    static State<Unit> Jump_E9 => JumpRelative("Jump_E9", RelNear);
    static State<Unit> Jcc_70_7F => ConditionalJump("Jcc_70_7F", Rel8);
    static State<Unit> Jcc_0F80_0F8F => ConditionalJump("Jcc_0F80_0F8F", RelNear);

    // JMP far ptr16:16 / ptr16:32 (0xEA)。
    static State<Unit> FarJump_EA =>
        from _1 in SetLog("FarJump_EA")
        from type in OperandType(true)
        from target in FarPointerIp(type)
        from _2 in FarTransfer(target)
        select unit;

    // CALL far ptr16:16 / ptr16:32 (0x9A): CS, (E)IP を push してから offset:segment へ far ジャンプ。
    // オペランドサイズに従う(32ビットコードではオフセットは 32 ビット、リターンフレームも 32 ビット幅)。
    static State<Unit> CallFar_9A =>
        from _1 in SetLog("CallFar_9A")
        from type in OperandType(true)
        from target in FarPointerIp(type)
        from _2 in PushReturnFar(type)
        from _3 in FarTransfer(target)
        select unit;

    // RET (0xC3) / RET imm16 (0xC2): 戻り番地を pop し、release バイトの引数を捨てる。
    static State<Unit> NearReturn(string name, State<ushort> release) =>
        from _1 in SetLog(name)
        from n in release
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in JmpTo(ret)
        from _3 in StackAdjust(n)
        select unit;

    // RETF (0xCB) / RETF imm16 (0xCA): (E)IP, CS を pop して far 復帰する(オペランドサイズに従う)。
    // 引数の解放は特権レベルが下がるときの外側スタックへの切り替えより前(内側スタック上)で行う。
    static State<Unit> FarReturn(string name, State<ushort> release) =>
        from _1 in SetLog(name)
        from n in release
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in JmpTo(ret)
        from cs in Pop(type)
        from _3 in StackAdjust(n)
        from _4 in When(2 == type, ReturnToOuterStack((ushort)cs.Value))
        from _5 in LoadSReg(1, (ushort)cs.Value)
        select unit;

    // 引数を捨てない RET/RETF(表の初期化順に依存しないよう、フィールドではなくその場で作る)。
    static State<ushort> NoRelease => ((ushort)0).ToState();

    static State<Unit> Ret_C3 => NearReturn("Ret_C3", NoRelease);
    static State<Unit> Ret_C2 => NearReturn("Ret_C2", GetMemoryDataIp16);
    static State<Unit> Retf_CB => FarReturn("Retf_CB", NoRelease);
    static State<Unit> Retf_CA => FarReturn("Retf_CA", GetMemoryDataIp16);

    // Group1 (0x80/0x81: r/m op imm、0x83: r/m op imm8 符号拡張)。即値は r/m のデータより先に取得する(高速コアと同じ順)。
    static State<Unit> Group1(string name, Func<int, State<Data>> immediate) =>
        from _1 in SetLog(name)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from imm in immediate(type)
        from _2 in Binary(Rm(addr, type), Const(imm), m.reg)
        select unit;

    static State<Unit> Group1_80_81 => Group1("Group1_80_81", GetMemoryDataIp_);
    static State<Unit> Group1_83 => Group1("Group1_83", Imm8SignExtended);

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

    static State<Unit> Lea_8D =>
        from _1 in SetLog("Lea_8D")
        from m in ModRegRm()
        // LEA はメモリを読まず、実効オフセット(セグメントベース非加算)を reg へ書く。
        from addr in GetMemOrRegOffset(m.mod, m.rm)
        from type in OperandType(true)
        from _2 in SetRegData(m.reg, addr.addr.ToTypeData(type))
        select unit;

    // MOV r/m, imm (0xC6/0xC7)
    static State<Unit> Mov_C6_C7 =>
        from _1 in SetLog("Mov_C6_C7")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from imm in GetMemoryDataIp_(type)
        from _2 in SetMemOrRegData(addr, imm)
        select unit;

    // moffs のオフセットを実効アドレスサイズで読む(32ビットコードでは32ビット、0x67で反転)。
    static State<uint> MoffsOffset =>
        from a32 in GetDataFromCpu(cpu => cpu.code32 != cpu.address_size_prefix)
        from off in a32 ? GetMemoryDataIp32 : GetMemoryDataIp16.Select(dw => (uint)dw)
        select off;

    // MOV AL/AX/EAX, [moffs] (0xA0/0xA1): 直接アドレス(DS:offset)から読み込む。
    static State<Unit> Mov_A0_A1 =>
        from _1 in SetLog("Mov_A0_A1")
        from offset in MoffsOffset
        from type in OperandTypeW
        from _2 in MovAccFromMoffs(type, offset)
        select unit;

    // MOV [moffs], AL/AX/EAX (0xA2/0xA3): アキュムレータを直接アドレス(DS:offset)へ書き込む。
    static State<Unit> Mov_A2_A3 =>
        from _1 in SetLog("Mov_A2_A3")
        from offset in MoffsOffset
        from type in OperandTypeW
        from _2 in MovMoffsFromAcc(type, offset)
        select unit;

    static State<Unit> Mov_8E =>
        from _1 in SetLog("Mov_8E")
        from m in ModRegRm()
        from data in GetMemOrRegData(m.mod, m.rm, true)
        from _2 in SetSReg3(m.reg, data.data)
        select unit;

    static State<Unit> Mov_0F20 =>
        from _1 in SetLog("Mov_0F20")
        from m in ModRegRm()
        where 3 == m.mod
        from data in GetCrReg(m.reg)
        from _3 in SetRegData32(m.rm, data)
        select unit;

    // MOV r32, DRx (0F 21) / MOV DRx, r32 (0F 23): デバッグレジスタ。
    // ハードウェアブレークポイントは未実装のため、値を保持するだけの汎用ストア。
    static State<Unit> Mov_0F21 =>
        from _1 in SetLog("Mov_0F21")
        from m in ModRegRm()
        where 3 == m.mod
        from data in GetDataFromEnvCpu((env, _) => env.Dr[m.reg])
        from _3 in SetRegData32(m.rm, data)
        select unit;

    static State<Unit> Mov_0F23 =>
        from _1 in SetLog("Mov_0F23")
        from m in ModRegRm()
        where 3 == m.mod
        from data in GetRegData32(m.rm)
        from _3 in Effect((env, _) => env.Dr[m.reg] = data)
        select unit;

    static State<Unit> Mov_0F22 =>
        from _1 in SetLog("Mov_0F22")
        from m in ModRegRm()
        where 3 == m.mod
        from data in GetRegData32(m.rm)
        from _3 in SetCrReg(m.reg, data)
        select unit;

    // CPUID (0F A2): 入力 EAX=リーフ。最小限の機能のみ報告し、APIC 等の高度な
    // (未エミュレートの MMIO を要する)経路に SeaBIOS が入らないようにする。
    static State<Unit> Cpuid_0FA2 =>
        from _1 in SetLog("Cpuid_0FA2")
        from leaf in GetRegData32(0) // EAX
        let result = CpuidLeaf(leaf)
        from _2 in SetCpu(cpu => { (cpu.eax, cpu.ebx, cpu.ecx, cpu.edx) = result; return cpu; })
        select unit;

    // CPUID の応答 (EAX, EBX, ECX, EDX)。
    //   リーフ 0: 最大リーフ + ベンダ文字列 "GenuineIntel"(EBX="Genu" EDX="ineI" ECX="ntel")。
    //   リーフ 1: family 6、拡張機能なし。EDX はエミュレートできる機能のみ:
    //             FPU(0x1) PSE(0x8) TSC(0x10) MSR(0x20) PAE(0x40) CX8(0x100) CMOV(0x8000)。
    //             686-pae カーネルはここで PAE を確認して CR4.PAE を設定する。APIC/SEP/FXSR/MMX 等は未実装のため伏せる。
    static (uint eax, uint ebx, uint ecx, uint edx) CpuidLeaf(uint leaf) => leaf switch
    {
        0 => (1, 0x756e6547, 0x6c65746e, 0x49656e69),
        1 => (0x00000600, 0, 0, 0x00008179),
        _ => (0, 0, 0, 0),
    };

    // RDTSC (0F 31): EDX:EAX <- タイムスタンプカウンタ。実行済み命令数を代用する
    // (Runner が毎命令 env.Tsc を更新する)。
    static State<Unit> Rdtsc_0F31 =>
        from _1 in SetLog("Rdtsc_0F31")
        from tsc in GetDataFromEnvCpu((env, _) => env.Tsc)
        from _2 in SetWide(2, tsc)
        select unit;

    // RDMSR (0F 32) / WRMSR (0F 30): ECX で選択した MSR を EDX:EAX で読み書きする。
    // 実際の機能は持たない汎用ストア(未書き込みの MSR は 0 を返す)。
    static State<Unit> Rdmsr_0F32 =>
        from _1 in SetLog("Rdmsr_0F32")
        from value in GetDataFromEnvCpu((env, cpu) => env.Msrs.GetValueOrDefault(cpu.ecx))
        from _2 in SetWide(2, value)
        select unit;

    static State<Unit> Wrmsr_0F30 =>
        from _1 in SetLog("Wrmsr_0F30")
        from _2 in Effect((env, cpu) => env.Msrs[cpu.ecx] = ((ulong)cpu.edx << 32) | cpu.eax)
        select unit;

    // CMPXCHG r/m, r (0F B0/B1): acc(AL/AX/EAX) と r/m を比較し、
    // 一致なら r/m <- reg、不一致なら acc <- r/m。フラグは CMP(acc, r/m) と同じ。
    static State<Unit> Cmpxchg_0FB0_B1 =>
        from _1 in SetLog("Cmpxchg_0FB0_B1")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        let rm = Rm(addr, type)
        let acc = Reg(0, type)
        from dst in rm.Read
        from old in acc.Read
        from _f in Calc(old, dst, 7) // CMP acc, r/m のフラグ
        from _2 in old.Value == dst.Value ? Move(rm, Reg(m.reg, type)) : acc.Write(dst)
        select unit;

    // XADD r/m, r (0F C0/C1): temp = r/m + reg(フラグは ADD)、reg <- r/m 旧値、r/m <- temp。
    static State<Unit> Xadd_0FC0_C1 =>
        from _1 in SetLog("Xadd_0FC0_C1")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        let rm = Rm(addr, type)
        let reg = Reg(m.reg, type)
        from dst in rm.Read
        from src in reg.Read
        from sum in Calc(dst, src, 0)
        from _2 in reg.Write(dst)
        from _3 in rm.Write(sum)
        select unit;

    // BSWAP r32 (0F C8+r): 32 ビットレジスタのバイト順を反転する(ModRM なし)。
    static State<Unit> Bswap_0FC8_CF =>
        from _1 in SetLog("Bswap_0FC8_CF")
        from opecode in Opecodes
        let reg = opecode[1] & 0x07
        from v in GetRegData32(reg)
        from _2 in SetRegData32(reg, ((v & 0xFF) << 24) | ((v & 0xFF00) << 8) | ((v >> 8) & 0xFF00) | (v >> 24))
        select unit;

    // Group9 (0F C7): /1 = CMPXCHG8B m64。EDX:EAX と m64 を比較し、
    // 一致なら m64 <- ECX:EBX, ZF=1、不一致なら EDX:EAX <- m64, ZF=0。
    static State<Unit> Group9_0FC7 =>
        from _1 in SetLog("Group9_0FC7")
        from m in ModRegRm()
        where m.reg == 1 && m.mod != 3
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from lo in GetMemOrRegData32(addr)
        from hi in GetMemOrRegData32((addr.isMem, addr.addr + 4))
        from cpu in GetCpu
        from _3 in cpu.eax == lo && cpu.edx == hi
            ? from _w1 in SetMemOrRegData(addr, cpu.ebx.ToTypeData())
              from _w2 in SetMemOrRegData((addr.isMem, addr.addr + 4), cpu.ecx.ToTypeData())
              from _z in _zf.Set(true)
              select unit
            : from _r in SetCpu(c => { c.eax = lo; c.edx = hi; return c; })
              from _z in _zf.Set(false)
              select unit
        select unit;

    /// 乗算の部品 ///////////////////////////////
    // 符号付き乗算の積と、type の幅に収まらなければ立てる CF=OF。
    static (long product, bool overflow) SignedMultiply(int type, long a, long b)
    {
        var product = a * b;
        var overflow = type switch
        {
            0 => product < sbyte.MinValue || product > sbyte.MaxValue,
            1 => product < short.MinValue || product > short.MaxValue,
            _ => product < int.MinValue || product > int.MaxValue,
        };
        return (product, overflow);
    }

    // 倍幅の結果を書く: 8 ビットは AX、16 ビットは DX:AX、32 ビットは EDX:EAX(MUL/IMUL と、RDTSC/RDMSR の EDX:EAX)。
    static State<Unit> SetWide(int type, ulong value) =>
        SetCpu(cpu =>
        {
            if (type == 0) cpu.ax = (ushort)value;
            else if (type == 1) { cpu.ax = (ushort)value; cpu.dx = (ushort)(value >> 16); }
            else { cpu.eax = (uint)value; cpu.edx = (uint)(value >> 32); }
            return cpu;
        });

    // IMUL r, ...(2/3 オペランド形式): 積の下位を reg へ、収まらなければ CF=OF=1(SF/ZF 等は未定義)。
    static State<Unit> ImulInto(int reg, int type, long a, long b)
    {
        var (product, overflow) = SignedMultiply(type, a, b);
        return from _1 in SetRegData(reg, ((uint)product).ToTypeData(type))
               from _2 in SetCpu((_cf, overflow), (_of, overflow))
               select unit;
    }

    // IMUL r, r/m (0F AF): 2オペランド符号付き乗算 r = r * r/m。
    static State<Unit> Imul_0FAF =>
        from _1 in SetLog("Imul_0FAF")
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        from dst in GetRegData(m.reg, src.data.type)
        from _2 in ImulInto(m.reg, src.data.type, dst.Signed, src.data.Signed)
        select unit;

    // SHLD/SHRD r/m, r, (imm8 | CL): ダブル精度シフト。
    //   0F A4=SHLD imm8, 0F A5=SHLD CL, 0F AC=SHRD imm8, 0F AD=SHRD CL
    static State<Unit> ShldShrd =>
        from _1 in SetLog("ShldShrd")
        from opecode in Opecodes
        let left = (opecode[1] & 0x08) == 0   // A4/A5=SHLD(左), AC/AD=SHRD(右)
        let useCl = (opecode[1] & 0x01) != 0  // A5/AD=CL, A4/AC=imm8
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from dst in GetMemOrRegData(addr, true)
        from src in GetRegData(m.reg, dst.type)
        from cnt in useCl ? GetRegData(1, 0).Select(d => (int)d.db) : GetMemoryDataIp8.Select(b => (int)b)
        let count = cnt & 0x1F
        // count==0 は結果・フラグとも不変
        from _2 in Unless(count == 0, ApplyDoubleShift(addr, dst.type, DoubleShift(dst.Value, src.Value, count, Bits(dst.type), left)))
        select unit;

    static State<Unit> ApplyDoubleShift(
        MemAddr addr, int type, (uint result, bool cf, bool of) r) =>
        from _w in SetMemOrRegData(addr, r.result.ToTypeData(type))
        from _f in SetCpu(
            (_cf, r.cf),
            (_of, r.of),
            (_zf, (r.result & Mask(type)) == 0),
            (_sf, (r.result & Msb(type)) != 0)
        )
        select unit;

    static (uint result, bool cf, bool of) DoubleShift(uint dst, uint src, int count, int bits, bool left)
    {
        uint mask = bits == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        uint msb = bits == 16 ? 0x8000u : 0x80000000u;
        ulong d = dst & mask, s = src & mask;
        int rest = bits - count; // 通常 1..bits-1(16bit で count>=16 は未定義。安全のため下でガード)
        if (rest <= 0) rest = bits;
        uint result;
        bool cf;
        if (left)
        {
            result = (uint)(((d << count) | (s >> rest)) & mask);
            cf = ((dst >> rest) & 1) != 0;         // dst から押し出された最後のビット
        }
        else
        {
            result = (uint)(((d >> count) | (s << rest)) & mask);
            cf = ((dst >> (count - 1)) & 1) != 0;
        }
        bool of = count == 1 && (((result & msb) != 0) != ((dst & msb) != 0));
        return (result, cf, of);
    }

    // BT/BTS/BTR/BTC r/m, r (0F A3/AB/B3/BB): ビット番号はレジスタ reg。
    //   ope2 の bit3-4 で op を選ぶ: A3->BT(0) AB->BTS(1) B3->BTR(2) BB->BTC(3)
    // メモリオペランドでは「ビット列アドレッシング」: ビット番号(符号付き)を
    // ワード単位でアドレスへ繰り込む(基底 + (bit>>5)*4)。Linux の set_bit/test_bit が
    // 32 を超えるビット番号で多用するため、これがないとビットマップ操作が全て壊れる。
    static State<Unit> BitTest_reg =>
        from _1 in SetLog("BitTest_reg")
        from opecode in Opecodes
        let op = (opecode[1] >> 3) & 0x3
        from m in ModRegRm()
        from addr0 in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(true)
        from bitData in GetRegData(m.reg, type)
        let bitRaw = bitData.Signed
        let addr = addr0.isMem
            ? (true, (uint)(addr0.addr + (bitRaw >> (type == 1 ? 4 : 5)) * (type == 1 ? 2 : 4)))
            : addr0
        from data in GetMemOrRegData(addr, true)
        from _2 in BitTest(addr, data, bitRaw & (type == 1 ? 15 : 31), op)
        select unit;

    // Group8 (0F BA): BT/BTS/BTR/BTC r/m, imm8。reg=4..7 で op を選ぶ。
    static State<Unit> Group8_0FBA =>
        from _1 in SetLog("Group8_0FBA")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, true)
        from imm in GetMemoryDataIp8
        from _2 in BitTest(addr, data, imm, m.reg & 0x3)
        select unit;

    // BSF/BSR r, r/m (0F BC/BD): ビットスキャン。BC=前方(最下位), BD=後方(最上位)。
    static State<Unit> BitScan_0FBC_BD =>
        from _1 in SetLog("BitScan_0FBC_BD")
        from opecode in Opecodes
        let forward = opecode[1] == 0xBC
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        from _2 in BitScan(m.reg, src.data, forward)
        select unit;

    // CMOVcc r, r/m (0F 40-4F): 条件成立時のみ r/m を reg へ転送する。
    static State<Unit> Cmov_0F40_4F =>
        from _1 in SetLog("Cmov_0F40_4F")
        from opecode in Opecodes
        let cond = opecode[1] & 0xF
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        from f in Jcc(cond)
        from _2 in When(f, SetRegData(m.reg, src.data))
        select unit;

    // SETcc r/m8 (0F 90-9F): 条件成立なら 1、不成立なら 0 を r/m8 に書く。
    static State<Unit> Setcc_0F90_9F =>
        from _1 in SetLog("Setcc_0F90_9F")
        from opecode in Opecodes
        let type = opecode[1] & 0xF
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from f in Jcc(type)
        from _2 in SetMemOrRegData(addr, ((byte)(f ? 1 : 0)).ToTypeData())
        select unit;

    // MOVZX/MOVSX reg, r/m8|r/m16 (0F B6/B7/BE/BF)
    //   bit0 of ope2: 0=ソース8bit, 1=ソース16bit
    //   0xBE/0xBF は符号拡張、0xB6/0xB7 はゼロ拡張。
    static State<Unit> MovzxMovsx_0FB6_BF =>
        from _1 in SetLog("MovzxMovsx_0FB6_BF")
        from opecode in Opecodes
        let srcW = 0 != (opecode[1] & 0x01)
        let signed = 0 != (opecode[1] & 0x08)
        from m in ModRegRm()
        // ソースサイズはオペコードで固定(8/16bit)のため、OperandType を介さず直接読む。
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from value in srcW
            ? GetMemOrRegData16(addr).Select(dw => signed ? (uint)(int)(short)dw : dw)
            : GetMemOrRegData8(addr).Select(db => signed ? (uint)(int)(sbyte)db : db)
        from type in OperandType(true)
        from _2 in SetRegData(m.reg, value.ToTypeData(type))
        select unit;

    static State<Unit> Mov_B0_BF =>
        from _1 in SetLog("Mov_B0_BF")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x08)
        let reg = opecode[0] & 0x07
        from data in GetMemoryDataIp(w)
        from _2 in SetRegData(reg, data.data)
        select unit;

    // d=1: reg <- r/m (0x8A/0x8B)、d=0: r/m <- reg (0x88/0x89)。転送元だけを読む(転送先のメモリは読まない)。
    static State<Unit> Mov_88_8B =>
        from _1 in SetLog("Mov_88_8B")
        from opecode in Opecodes
        let d = 0 != (opecode[0] & 0x02)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from _2 in d ? Move(Reg(m.reg, type), Rm(addr, type)) : Move(Rm(addr, type), Reg(m.reg, type))
        select unit;

    static State<Unit> Arithmetic =>
        from _1 in SetLog("Arithmetic")
        from opecode in Opecodes
        let kind = (opecode[0] >> 3) & 0x7
        let form = opecode[0] & 0x7
        // 下位3ビットが 4/5 のものは ModRM を持たない AL/AX/EAX, imm 形式。
        from _2 in (form < 4) ? ArithmeticModRM(kind) : ArithmeticAccImm(kind, 0 != (form & 1))
        select unit;

    // d=1 (02/03 など): reg <- reg op r/m、d=0 (00/01 など): r/m <- r/m op reg。
    static State<Unit> ArithmeticModRM(int kind) =>
        from opecode in Opecodes
        let d = 0 != (opecode[0] & 2)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from _ in d ? Binary(Reg(m.reg, type), Rm(addr, type), kind) : Binary(Rm(addr, type), Reg(m.reg, type), kind)
        select unit;

    static State<Unit> ArithmeticAccImm(int kind, bool w) =>
        from type in OperandType(w)
        from imm in GetMemoryDataIp_(type)
        from _ in Binary(Reg(0, type), Const(imm), kind)
        select unit;

    // Group2(シフト/ローテート): r/m を count 回。シフト量は 1 (D0/D1)、CL (D2/D3)、imm8 (C0/C1)。
    static State<Unit> ShiftRotate(string name, State<int> count) =>
        from _1 in SetLog(name)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        let rm = Rm(addr, type)
        from data in rm.Read
        from n in count
        from result in Group2(data, n, m.reg)
        from _2 in rm.Write(result)
        select unit;

    static State<Unit> Group2_D0_D1 => ShiftRotate("Group2_D0_D1", 1.ToState());
    static State<Unit> Group2_D2_D3 => ShiftRotate("Group2_D2_D3", _cl.Get().Select(cl => (int)cl));
    static State<Unit> Group2_C0_C1 => ShiftRotate("Group2_C0_C1", GetMemoryDataIp8.Select(imm => (int)imm));

    // Group3 (0xF6/0xF7): reg で TEST/NOT/NEG/MUL/IMUL/DIV/IDIV を選ぶ。
    static State<Unit> Group3_F6_F7 =>
        from _1 in SetLog("Group3_F6_F7")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from data in Rm(addr, type).Read
        from _2 in Group(
            m.reg,
            (0, Group3_Test(data)),
            (1, Group3_Test(data)),
            (2, Group3_Not(addr, data)),
            (3, Group3_Neg(addr, data)),
            (4, Group3_Mul(data)),
            (5, Group3_Imul(data)),
            (6, Group3_Div(data)),
            (7, Group3_Idiv(data))
        )
        select unit;

    // TEST r/m, imm : AND の結果を捨ててフラグ(CF=OF=0, ZF/SF)だけ更新する。
    static State<Unit> Group3_Test(Data data) =>
        from imm in GetMemoryDataIp_(data.type)
        from _ in Test(Const(data), Const(imm))
        select unit;

    // NOT r/m : ビット反転（フラグ変化なし）。
    static State<Unit> Group3_Not(MemAddr addr, Data data) =>
        SetMemOrRegData(addr, (~data.Value).ToTypeData(data.type));

    // NEG r/m : 0 - r/m。フラグは SUB と同じ。
    static State<Unit> Group3_Neg(MemAddr addr, Data data) =>
        from _f in update_eflags_sub(data.type, 0, data.Value)
        from _w in SetMemOrRegData(addr, (0u - data.Value).ToTypeData(data.type))
        select unit;

    // MUL r/m : 符号なし乗算。AL*r/m8->AX, AX*r/m16->DX:AX, EAX*r/m32->EDX:EAX。上位が 0 でなければ CF=OF=1。
    static State<Unit> Group3_Mul(Data data) =>
        from acc in GetRegData(0, data.type)
        let product = (ulong)acc.Value * data.Value
        let upper = product >> Bits(data.type)
        from _1 in SetWide(data.type, product)
        from _2 in SetCpu((_cf, upper != 0), (_of, upper != 0))
        select unit;

    // IMUL r/m : 符号付き乗算。積が下位の幅に収まらなければ CF=OF=1。
    static State<Unit> Group3_Imul(Data data) =>
        from acc in GetRegData(0, data.type)
        let result = SignedMultiply(data.type, acc.Signed, data.Signed)
        from _1 in SetWide(data.type, (ulong)result.product)
        from _2 in SetCpu((_cf, result.overflow), (_of, result.overflow))
        select unit;

    // IMUL r, r/m, imm (0x69 imm16/32, 0x6B imm8符号拡張): 3オペランド符号付き乗算。
    //   r = r/m * imm。結果が転送先サイズに収まらなければ CF=OF=1（SF/ZF等は未定義）。
    static State<Unit> Imul_69_6B =>
        from _1 in SetLog("Imul_69_6B")
        from opecode in Opecodes
        let imm8 = 0 != (opecode[0] & 0x02)   // 0x6B は imm8、0x69 は imm16/32
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        let type = src.data.type              // w=true なので 1(16bit) か 2(32bit)
        from imm in imm8 ? Imm8SignExtended(type) : GetMemoryDataIp_(type)
        from _2 in ImulInto(m.reg, type, src.data.Signed, imm.Signed)
        select unit;

    // DIV r/m : 符号なし除算。商と剰余を AL/AH, AX/DX, EAX/EDX へ。
    static State<Unit> Group3_Div(Data data) =>
        SetCpu(c =>
        {
            if (data.type == 0)
            {
                if (data.db == 0) throw new DivideByZeroException();
                uint dividend = c.ax;
                c.al = (byte)(dividend / data.db);
                c.ah = (byte)(dividend % data.db);
            }
            else if (data.type == 1)
            {
                if (data.dw == 0) throw new DivideByZeroException();
                uint dividend = ((uint)c.dx << 16) | c.ax;
                c.ax = (ushort)(dividend / data.dw);
                c.dx = (ushort)(dividend % data.dw);
            }
            else
            {
                if (data.dd == 0) throw new DivideByZeroException();
                ulong dividend = ((ulong)c.edx << 32) | c.eax;
                c.eax = (uint)(dividend / data.dd);
                c.edx = (uint)(dividend % data.dd);
            }
            return c;
        });

    // IDIV r/m : 符号付き除算。
    static State<Unit> Group3_Idiv(Data data) =>
        SetCpu(c =>
        {
            if (data.type == 0)
            {
                if (data.db == 0) throw new DivideByZeroException();
                int dividend = (short)c.ax;
                c.al = (byte)(sbyte)(dividend / (sbyte)data.db);
                c.ah = (byte)(sbyte)(dividend % (sbyte)data.db);
            }
            else if (data.type == 1)
            {
                if (data.dw == 0) throw new DivideByZeroException();
                int dividend = (int)(((uint)c.dx << 16) | c.ax);
                c.ax = (ushort)(short)(dividend / (short)data.dw);
                c.dx = (ushort)(short)(dividend % (short)data.dw);
            }
            else
            {
                if (data.dd == 0) throw new DivideByZeroException();
                long dividend = (long)(((ulong)c.edx << 32) | c.eax);
                c.eax = (uint)(dividend / (int)data.dd);
                c.edx = (uint)(dividend % (int)data.dd);
            }
            return c;
        });

    // INC r/m: type に応じて +1 し、フラグ(CF以外)を更新して書き戻す。
    static State<Unit> IncData(MemAddr addr, Data data) =>
        from _f in update_eflags_inc(data)
        from _w in SetMemOrRegData(addr, (data.Value + 1).ToTypeData(data.type))
        select unit;

    // DEC r/m: type に応じて -1 し、フラグ(CF以外)を更新して書き戻す。
    static State<Unit> DecData(MemAddr addr, Data data) =>
        from _f in update_eflags_dec(data)
        from _w in SetMemOrRegData(addr, (data.Value - 1).ToTypeData(data.type))
        select unit;

    // Group4 (0xFE): reg=0 INC r/m8, reg=1 DEC r/m8
    static State<Unit> Group4_FE =>
        from _1 in SetLog("Group4_FE")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, false)
        from _2 in Group(
            m.reg,
            (0, IncData(addr, data)),
            (1, DecData(addr, data))
        )
        select unit;

    // Group5 (0xFF): INC/DEC/CALL/JMP r/m, PUSH r/m
    static State<Unit> Group5_FF =>
        from _1 in SetLog("Group5_FF")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, true)
        from _2 in Group(
            m.reg,
            (0, IncData(addr, data)),
            (1, DecData(addr, data)),
            (2, Call_rm(data)),               // 近傍間接 CALL
            (3, CallFar_rm(addr, data.type)), // far 間接 CALL m16:16/m16:32
            (4, JmpTo(data)),                 // 近傍間接 JMP
            (5, JmpFar_rm(addr, data.type)),  // far 間接 JMP m16:16/m16:32
            (6, Push(data))                   // PUSH r/m
        )
        select unit;

    // far 間接 JMP: メモリから offset(16/32bit) と selector(16bit) を読み、CS:EIP を切り替える。
    static State<Unit> JmpFar_rm(MemAddr addr, int type) =>
        from target in FarPointerMem(addr, type)
        from _ in FarTransfer(target)
        select unit;

    // far 間接 CALL: CS と IP/EIP をオペランドサイズ幅で push してから far ジャンプする。
    static State<Unit> CallFar_rm(MemAddr addr, int type) =>
        from _1 in PushReturnFar(type)
        from _2 in JmpFar_rm(addr, type)
        select unit;

    // 近傍間接 CALL: 戻り番地(現在のIP/EIP)を push してから IP/EIP を target に設定する。
    static State<Unit> Call_rm(Data target) =>
        from _1 in PushReturnNear(target.type)
        from _2 in JmpTo(target)
        select unit;

    // LES (0xC4) / LDS (0xC5): メモリの far ポインタ m16:16/m16:32 を読み、
    // オフセットを reg、セレクタを ES/DS へロードする。レジスタ形式(mod=3)は #UD 扱いで停止。
    // (NTLDR 等の 16bit リアルモードコードが多用する。Linux は使わないため未実装だった。)
    static State<Unit> LesLds_C4_C5 =>
        from _1 in SetLog("LesLds_C4_C5")
        from opecode in Opecodes
        from _2 in LoadFarPtr(opecode[0] == 0xC4 ? 0 : 3) // ES / DS
        select unit;

    // LSS (0F B2) / LFS (0F B4) / LGS (0F B5): 2バイト版の far ポインタロード。
    static State<Unit> LssLfsLgs_0FB2_B5 =>
        from _1 in SetLog("LssLfsLgs_0FB2_B5")
        from opecode in Opecodes
        from _2 in LoadFarPtr(opecode[1] == 0xB2 ? 2 : opecode[1] == 0xB4 ? 4 : 5) // SS / FS / GS
        select unit;

    // far ポインタ m16:16/m16:32 を読み、オフセットを reg、セレクタを sreg へロードする共通部。
    static State<Unit> LoadFarPtr(int sreg) =>
        from m in ModRegRm()
        where 3 != m.mod
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(true)
        from pointer in FarPointerMem(addr, type)
        from _3 in SetRegData(m.reg, pointer.offset.ToTypeData(type))
        from _4 in LoadSReg(sreg, pointer.segment)
        select unit;

    // WAIT/FWAIT (0x9B): FPU 例外を同期する。例外は配送しないため NOP。
    static State<Unit> Fwait_9B =>
        from _ in SetLog("Fwait_9B")
        select unit;

    // INVD (0F 08) / WBINVD (0F 09): キャッシュ無効化/書き戻し。エミュレータでは NOP。
    static State<Unit> CacheInvd_0F08_09 =>
        from _ in SetLog("CacheInvd")
        select unit;

    // INT imm8 (0xCD): 指定ベクタへソフトウェア割り込み。
    static State<Unit> Int_CD =>
        from _1 in SetLog("Int_CD")
        from vector in GetMemoryDataIp8
        from cpu in GetCpu
        from _2 in cpu.vm ? InterruptFromV86(vector) : Interrupt(vector)
        select unit;


    // INT3 (0xCC): ベクタ 3 へのブレークポイント割り込み。
    static State<Unit> Int3_CC =>
        from _1 in SetLog("Int3_CC")
        from _2 in Interrupt(3)
        select unit;

    // INTO (0xCE): OF=1 のときのみベクタ 4 へ割り込む。
    static State<Unit> Into_CE =>
        from _1 in SetLog("Into_CE")
        from of in GetDataFromCpu(cpu => cpu.of)
        from _2 in When(of, Interrupt(4))
        select unit;

    // IRET (0xCF): IP, CS, FLAGS を pop して割り込みから復帰する。
    static State<Unit> Iret_CF =>
        from _1 in SetLog("Iret_CF")
        from _2 in Iret
        select unit;

    // CLI/STI: 割り込み許可フラグ(IF、この実装では jf)を操作する。
    static State<Unit> Cli_FA =>
        from _1 in SetLog("Cli_FA")
        from _2 in _jf.Set(false)
        select unit;

    static State<Unit> Sti_FB =>
        from _1 in SetLog("Sti_FB")
        from _2 in _jf.Set(true)
        select unit;

    static State<Unit> Cld_FC =>
        from _1 in SetLog("Cld_FC")
        from _2 in _df.Set(false)
        select unit;

    static State<Unit> Std_FD =>
        from _1 in SetLog("Std_FD")
        from _2 in _df.Set(true)
        select unit;

    // TEST r/m, r (0x84/0x85): AND の結果を捨ててフラグだけ更新する。
    static State<Unit> Test_84_85 =>
        from _1 in SetLog("Test_84_85")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from _2 in Test(Rm(addr, type), Reg(m.reg, type))
        select unit;

    // TEST AL/AX/EAX, imm (0xA8/0xA9)
    static State<Unit> Test_A8_A9 =>
        from _1 in SetLog("Test_A8_A9")
        from type in OperandTypeW
        from imm in GetMemoryDataIp_(type)
        from _2 in Test(Reg(0, type), Const(imm))
        select unit;

    // XCHG r/m, r (0x86/0x87)
    static State<Unit> Xchg_86_87 =>
        from _1 in SetLog("Xchg_86_87")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from _2 in Swap(Rm(addr, type), Reg(m.reg, type))
        select unit;

    static State<Unit> Xchg_91_97 =>
        from _1 in SetLog("Xchg_91_97")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from _2 in Swap(Reg(0, type), Reg(reg, type))
        select unit;

    // INC r (40-47) / DEC r (48-4F): オペコード下位 3 ビットのレジスタをオペランドサイズ幅で。
    static State<Unit> IncDecReg(string name, Func<MemAddr, Data, State<Unit>> step) =>
        from _1 in SetLog(name)
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from value in GetRegData(reg, type)
        from _2 in step((false, (uint)reg), value)
        select unit;

    static State<Unit> Inc_40_47 => IncDecReg("Inc_40_47", IncData);
    static State<Unit> Dec_48_4F => IncDecReg("Dec_48_4F", DecData);

    // CALL rel16/rel32 (0xE8): rel 読み取り後の IP/EIP（＝次命令アドレス）を戻り番地として push する。
    static State<Unit> Call_E8 =>
        from _1 in SetLog("Call_E8")
        from type in OperandType(true)
        from offset in RelNear
        from _2 in PushReturnNear(type)
        from _3 in IpInc(offset)
        select unit;

    // PUSHA/PUSHAD (0x60): AX,CX,DX,BX,(開始時 SP),BP,SI,DI をオペランドサイズ幅で push する。
    static State<Unit> Pusha_60 =>
        from _1 in SetLog("Pusha_60")
        from type in OperandType(true)
        from sp0 in GetRegData(4, type) // 開始時点の (E)SP
        from _2 in Enumerable.Range(0, 8)
            .Select(reg => Move(Stack(type), reg == 4 ? Const(sp0) : Reg(reg, type)))
            .Sequence()
            .Ignore()
        select unit;

    // POPA/POPAD (0x61): DI,SI,BP,(SP読み飛ばし),BX,DX,CX,AX の順でオペランドサイズ幅で pop する。SP は破棄。
    static State<Unit> Popa_61 =>
        from _1 in SetLog("Popa_61")
        from type in OperandType(true)
        from _2 in Enumerable.Sequence(7, 0, -1) // DI(7) から AX(0) へ
            .Select(reg => reg == 4 ? Pop(type).Ignore() : Move(Reg(reg, type), Stack(type)))
            .Sequence()
            .Ignore()
        select unit;

    // ENTER imm16, imm8 (0xC8): スタックフレームを構築する。
    static State<Unit> Enter_C8 =>
        from _1 in SetLog("Enter_C8")
        from alloc in GetMemoryDataIp16
        from level in GetMemoryDataIp8
        from _2 in Enter(alloc, level)
        select unit;

    // LEAVE (0xC9): (E)SP <- (E)BP; (E)BP <- pop()。スタックフレームを破棄する。
    static State<Unit> Leave_C9 =>
        from _1 in SetLog("Leave_C9")
        from type in OperandType(true)
        from _2 in SetCpu(cpu => { if (cpu.stack32) { cpu.esp = cpu.ebp; } else { cpu.sp = cpu.bp; } return cpu; })
        from val in Pop(type)
        from _3 in type == 2 ? _ebp.Set(val.dd) : _bp.Set(val.dw)
        select unit;

    // PUSH/POP FS/GS (0F A0/A1/A8/A9)。オペランドサイズ幅で push/pop する。
    //   A0=PUSH FS, A1=POP FS, A8=PUSH GS, A9=POP GS。セグメント番号 FS=4, GS=5。
    static State<Unit> PushPopFsGs =>
        from _1 in SetLog("PushPopFsGs")
        from opecode in Opecodes
        let reg = (opecode[1] & 0x08) != 0 ? 5 : 4   // A8/A9=GS, A0/A1=FS
        let pop = (opecode[1] & 0x01) != 0           // A1/A9=POP
        from type in OperandType(true)
        from _2 in pop ? PopSregInto(reg, type) : PushSregOf(reg, type)
        select unit;

    // セグメントレジスタをオペランドサイズ幅で push する(32 ビットならセレクタをゼロ拡張)/ pop して下位 16 ビットをロードする。
    static State<Unit> PushSregOf(int reg, int type) =>
        from selector in GetSRegData(reg)
        from _ in Push(((uint)selector).ToTypeData(type))
        select unit;

    static State<Unit> PopSregInto(int reg, int type) =>
        from value in Pop(type)
        from _ in LoadSReg(reg, (ushort)value.Value)
        select unit;

    // PUSH Sreg (06/0E/16/1E): opcode>>3 が ES/CS/SS/DS の順のセグメント番号になる。
    static State<Unit> PushSreg =>
        from _1 in SetLog("PushSreg")
        from opecode in Opecodes
        from type in OperandType(true)
        from _2 in PushSregOf(opecode[0] >> 3, type)
        select unit;

    // POP Sreg (07/17/1F)。オペランドサイズ幅で pop し、下位16ビットをロードする。
    // プロテクトモードでは記述子を再ロードする。
    static State<Unit> PopSreg =>
        from _1 in SetLog("PopSreg")
        from opecode in Opecodes
        from type in OperandType(true)
        from _2 in PopSregInto(opecode[0] >> 3, type)
        select unit;

    // PUSH r (50-57) / POP r (58-5F): スタックとレジスタの間の Move。
    static State<Unit> Push_50_57 =>
        from _1 in SetLog("Push_50_57")
        from opecode in Opecodes
        from type in OperandType(true)
        from _2 in Move(Stack(type), Reg(opecode[0] & 0x07, type))
        select unit;

    static State<Unit> Pop_58_5F =>
        from _1 in SetLog("Pop_58_5F")
        from opecode in Opecodes
        from type in OperandType(true)
        from _2 in Move(Reg(opecode[0] & 0x07, type), Stack(type))
        select unit;

    // POP r/m (0x8F /0)
    static State<Unit> Pop_8F =>
        from _1 in SetLog("Pop_8F")
        from m in ModRegRm()
        where 0 == m.reg
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(true)
        from _3 in Move(Rm(addr, type), Stack(type))
        select unit;

    // PUSH imm16/imm32 (0x68): オペランドサイズで imm を読み、その幅で push する。
    static State<Unit> PushImm_68 =>
        from _1 in SetLog("PushImm_68")
        from type in OperandType(true)
        from value in GetMemoryDataIp_(type)
        from _2 in Push(value)
        select unit;

    // PUSH imm8 (0x6A): imm8 を符号拡張し、オペランドサイズ幅で push する。
    static State<Unit> PushImm_6A =>
        from _1 in SetLog("PushImm_6A")
        from type in OperandType(true)
        from imm in Imm8SignExtended(type)
        from _2 in Push(imm)
        select unit;

    // PUSHF/POPF はオペランドサイズに従う(32ビットコードでは EFLAGS 全体を 4 バイトで push/pop)。
    static State<Unit> Pushf_9C =>
        from _1 in SetLog("Pushf_9C")
        from type in OperandType(true)
        from fl in GetDataFromCpu(cpu => cpu.eflags)
        from _2 in Push(fl.ToTypeData(type))
        select unit;

    static State<Unit> Popf_9D =>
        from _1 in SetLog("Popf_9D")
        from type in OperandType(true)
        from fl in Pop(type)
        from _2 in SetCpu(cpu =>
        {
            var nv = type == 2 ? fl.dd : (cpu.eflags & 0xFFFF0000) | fl.dw;
            if (cpu.pe && cpu.cpl != 0)
            {
                // CPL>0(V86 モードも CPL=3): IOPL は変更不可。CPL>IOPL なら IF も変更不可。VM/RF は POPF では変わらない。
                const uint IOPL = 0x3000, IF = 0x200, VM = 0x20000, RF = 0x10000;
                var keep = IOPL | VM | RF;
                if (cpu.cpl > ((cpu.eflags >> 12) & 3)) keep |= IF;
                nv = (nv & ~keep) | (cpu.eflags & keep);
            }
            cpu.eflags = nv;
            return cpu;
        })
        select unit;

    // SAHF (0x9E): AH の下位8bit を FLAGS の下位8bit(SF/ZF/AF/PF/CF)へ転送する。
    static State<Unit> Sahf_9E =>
        from _1 in SetLog("Sahf_9E")
        from _2 in SetCpu(cpu =>
        {
            // FLAGS 下位8bit のうち bit1 は常に 1。AH をそのまま反映する。
            cpu.eflags = (cpu.eflags & 0xFFFFFF00) | cpu.ah | 0x02u;
            return cpu;
        })
        select unit;

    // LAHF (0x9F): FLAGS の下位8bit を AH へ転送する。
    static State<Unit> Lahf_9F =>
        from _1 in SetLog("Lahf_9F")
        from _2 in SetCpu(cpu => { cpu.ah = (byte)(cpu.eflags & 0xFF); return cpu; })
        select unit;

    // MOV r/m, Sreg (0x8C): セグメントレジスタを r/m16 へ格納する。
    static State<Unit> Mov_8C =>
        from _1 in SetLog("Mov_8C")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from sreg in GetSRegData(m.reg)
        from _2 in SetMemOrRegData(addr, sreg.ToTypeData())
        select unit;

    // CBW/CWDE (0x98): オペランドサイズ 16 なら AL→AX、32 なら AX→EAX の符号拡張。
    static State<Unit> Cbw_98 =>
        from _1 in SetLog("Cbw_98")
        from type in OperandType(true)
        from _2 in SetCpu(cpu =>
        {
            if (type == 2) cpu.eax = (uint)(int)(short)cpu.ax;
            else cpu.ax = (ushort)(short)(sbyte)cpu.al;
            return cpu;
        })
        select unit;

    // CWD/CDQ (0x99): オペランドサイズ 16 なら DX:AX、32 なら EDX:EAX の符号拡張。
    static State<Unit> Cwd_99 =>
        from _1 in SetLog("Cwd_99")
        from type in OperandType(true)
        from _2 in SetCpu(cpu =>
        {
            if (type == 2) cpu.edx = (int)cpu.eax < 0 ? 0xFFFFFFFF : 0;
            else cpu.dx = (ushort)((short)cpu.ax < 0 ? 0xFFFF : 0x0000);
            return cpu;
        })
        select unit;

    // XLAT (0xD7): AL <- [DS:BX + AL]
    static State<Unit> Xlat_D7 =>
        from _1 in SetLog("Xlat_D7")
        from _2 in Xlat
        select unit;

    // BCD 調整命令群。Linux は使わないが、Windows の HAL/NTLDR 等の 16/32bit コードが使う。
    static bool ParityEven(byte v) => (System.Numerics.BitOperations.PopCount(v) & 1) == 0;

    // AAA (0x37) / AAS (0x3F): 加減算後の非パック BCD 調整。
    static State<Unit> AaaAas_37_3F =>
        from _1 in SetLog("AaaAas_37_3F")
        from opecode in Opecodes
        from _2 in SetCpu(cpu =>
        {
            bool adjust = (cpu.al & 0x0F) > 9 || cpu.af;
            if (adjust)
            {
                if (opecode[0] == 0x37) { cpu.al = (byte)(cpu.al + 6); cpu.ah = (byte)(cpu.ah + 1); }
                else { cpu.al = (byte)(cpu.al - 6); cpu.ah = (byte)(cpu.ah - 1); }
            }
            cpu.af = adjust; cpu.cf = adjust;
            cpu.al = (byte)(cpu.al & 0x0F);
            return cpu;
        })
        select unit;

    // DAA (0x27) / DAS (0x2F): 加減算後のパック BCD 調整。
    static State<Unit> DaaDas_27_2F =>
        from _1 in SetLog("DaaDas_27_2F")
        from opecode in Opecodes
        from _2 in SetCpu(cpu =>
        {
            byte old = cpu.al; bool oldCf = cpu.cf; bool sub = opecode[0] == 0x2F;
            bool af = (old & 0x0F) > 9 || cpu.af;
            bool cf = oldCf || old > 0x99;
            int al = old;
            if (af) al = sub ? al - 6 : al + 6;
            if (cf) al = sub ? al - 0x60 : al + 0x60;
            cpu.al = (byte)al;
            cpu.af = af; cpu.cf = cf;
            cpu.zf = cpu.al == 0; cpu.sf = (cpu.al & 0x80) != 0; cpu.pf = ParityEven(cpu.al);
            return cpu;
        })
        select unit;

    // AAM (D4 ib) / AAD (D5 ib): AL を基数 ib で分解 / AH:AL を基数 ib で結合。
    static State<Unit> AamAad_D4_D5 =>
        from _1 in SetLog("AamAad_D4_D5")
        from opecode in Opecodes
        from imm in GetMemoryDataIp8
        where opecode[0] != 0xD4 || imm != 0 // AAM 0 は #DE
        from _3 in SetCpu(cpu =>
        {
            if (opecode[0] == 0xD4) { byte al = cpu.al; cpu.ah = (byte)(al / imm); cpu.al = (byte)(al % imm); }
            else { cpu.al = (byte)(cpu.al + cpu.ah * imm); cpu.ah = 0; }
            cpu.zf = cpu.al == 0; cpu.sf = (cpu.al & 0x80) != 0; cpu.pf = ParityEven(cpu.al);
            return cpu;
        })
        select unit;

    static State<Unit> Nop_90 =>
        from _ in SetLog("Nop_90")
        select unit;

    // HLT: 印を立てるだけ。待機(次の割り込みまで仮想時間を進める)はランナーが行う。
    static State<Unit> Hlt_F4 =>
        from _1 in SetLog("Hlt_F4")
        from _2 in SetCpu(cpu => { cpu.Halted = true; return cpu; })
        select unit;

    static State<Unit> Clc_F8 =>
        from _1 in SetLog("Clc_F8")
        from _2 in _cf.Set(false)
        select unit;

    static State<Unit> Stc_F9 =>
        from _1 in SetLog("Stc_F9")
        from _2 in _cf.Set(true)
        select unit;

    static State<Unit> Cmc_F5 =>
        from _1 in SetLog("Cmc_F5")
        from _2 in _cf.Modify(cf => !cf)
        select unit;

    // LOOP系 (0xE0-0xE2): CX-- してから、条件を満たせば rel8 分岐する。
    //   0xE2 LOOP   : CX!=0
    //   0xE1 LOOPE  : CX!=0 && ZF==1
    //   0xE0 LOOPNE : CX!=0 && ZF==0
    static State<Unit> Loop_E0_E2 =>
        from _1 in SetLog("Loop_E0_E2")
        from opecode in Opecodes
        from offset in Rel8
        from cx in GetRegData16(1)
        let newCx = (ushort)(cx - 1)
        from _2 in SetRegData16(1, newCx)
        from zf in _zf.Get()
        let taken = (opecode[0] & 0x03) switch
        {
            0 => newCx != 0 && !zf,   // LOOPNE
            1 => newCx != 0 && zf,    // LOOPE
            _ => newCx != 0,          // LOOP
        }
        from _3 in When(taken, IpInc(offset))
        select unit;

    // JCXZ (0xE3): CX==0 なら rel8 分岐（CX は変更しない）。
    static State<Unit> Jcxz_E3 =>
        from _1 in SetLog("Jcxz_E3")
        from offset in Rel8
        from cx in GetRegData16(1)
        from _2 in When(cx == 0, IpInc(offset))
        select unit;

    /// ポート入出力 ///////////////////////////////
    // IN acc, port / OUT port, acc。port は imm8(E4-E7)か DX(EC-EF)。
    // 幅は width で与える: imm8 形式は w ビットだけで決まり(8/16 ビット)、DX 形式はオペランドサイズに従う。
    static State<Unit> In(string name, State<int> width, State<int> port) =>
        from _1 in SetLog(name)
        from type in width
        from address in port
        from value in GetDataFromEnvCpu((env, _) => EnvInPortN(env, address, Bits(type) / 8))
        from _2 in SetRegData(0, value.ToTypeData(type))
        select unit;

    static State<Unit> Out(string name, State<int> width, State<int> port) =>
        from _1 in SetLog(name)
        from type in width
        from address in port
        from acc in GetRegData(0, type)
        from _2 in Effect((env, _) => EnvOutPortN(env, address, Bits(type) / 8, acc.Value))
        select unit;

    static State<int> ByteOrWord => Opecodes.Select(opecode => opecode[0] & 0x01);
    static State<int> PortImm8 => GetMemoryDataIp8.Select(port => (int)port);
    static State<int> PortDx => _dx.Get().Select(dx => (int)dx);

    static State<Unit> In_E4_E5 => In("In_E4_E5", ByteOrWord, PortImm8);
    static State<Unit> Out_E6_E7 => Out("Out_E6_E7", ByteOrWord, PortImm8);
    static State<Unit> In_EC_ED => In("In_EC_ED", OperandTypeW, PortDx);
    static State<Unit> Out_EE_EF => Out("Out_EE_EF", OperandTypeW, PortDx);

    // SGDT/SIDT (0F 01 /0, /1): limit(16bit) + base(32bit) をメモリへ書き出す。
    static State<Unit> Sgdt(int mod, int rm, bool idt) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from cpu in GetCpu
        from _1 in SetMemOrRegData(addr, (idt ? cpu.idt_limit : cpu.gdt_limit).ToTypeData())
        from _2 in SetMemOrRegData((addr.isMem, addr.addr + 2), (idt ? cpu.idt_base : cpu.gdt_base).ToTypeData())
        select unit;

    // LGDT/LIDT (0F 01 /2, /3): メモリから limit(16bit) + base(32bit) を読み込む。
    static State<Unit> Lgdt(int mod, int rm, bool idt) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from dw in GetMemOrRegData16(addr)
        from dd in GetMemOrRegData32((addr.isMem, addr.addr + 2))
        from _ in SetCpu(cpu =>
        {
            if (idt) { cpu.idt_base = dd; cpu.idt_limit = dw; }
            else { cpu.gdt_base = dd; cpu.gdt_limit = dw; }
            return cpu;
        })
        select unit;

    // Group6 (0F 00): SLDT/STR/LLDT/LTR/VERR/VERW。LDT・タスクスイッチは未使用のため、
    // LLDT/LTR はセレクタを保持するだけ。VERR/VERW は記述子を照合して ZF に入れる(Linux は MDS 緩和の `verw` を頻繁に実行する)。
    static State<Unit> Group6_0F00 =>
        from _1 in SetLog("Group6_0F00")
        from m in ModRegRm()
        from _ in Group(
            m.reg,
            (0, SldtStr(m)),            // SLDT
            (1, SldtStr(m)),            // STR
            (2, LldtLtr(m)),            // LLDT
            (3, LldtLtr(m)),            // LTR
            (4, Verify(m, write: false)), // VERR
            (5, Verify(m, write: true))   // VERW
        )
        select unit;

    // VERR/VERW: セグメントが CPL/RPL から読める(書ける)なら ZF=1、そうでなければ ZF=0。例外は出さない。
    static State<Unit> Verify((int mod, int reg, int rm) m, bool write) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from sel in GetMemOrRegData16(addr)
        from accessible in GetDataFromEnvCpu((env, cpu) => EnvSegmentAccessible(env, cpu, sel, write))
        from _ in _zf.Set(accessible)
        select unit;

    // LAR/LSL (0F 02/03): r <- アクセス権 / リミット、ZF=1。照合に通らなければ ZF=0 で r は変えない。
    static State<Unit> Lar_0F02 => LarLsl("Lar_0F02", limit: false);
    static State<Unit> Lsl_0F03 => LarLsl("Lsl_0F03", limit: true);

    static State<Unit> LarLsl(string name, bool limit) =>
        from _1 in SetLog(name)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from sel in GetMemOrRegData16(addr)
        from cpu in GetCpu
        from value in GetDataFromEnvCpu((env, c) => EnvDescriptorQuery(env, c, sel, limit))
        from _2 in _zf.Set(value != null)
        from _3 in When(value != null, SetRegData(m.reg, cpu.code32 != cpu.operand_size_prefix ? (value ?? 0).ToTypeData() : ((ushort)(value ?? 0)).ToTypeData()))
        select unit;

    // SLDT/STR: 保持しているセレクタを返す(レジスタ形式は32bit幅のときゼロ拡張)。
    static State<Unit> SldtStr((int mod, int reg, int rm) m) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from cpu in GetCpu
        let sel = m.reg == 1 ? cpu.tr : cpu.ldtr
        from _ in addr.isMem ? SetMemOrRegData(addr, sel.ToTypeData()) : SetRegData(m.rm, ((uint)sel).ToTypeData())
        select unit;

    // LLDT/LTR: セレクタを保持するだけ(記述子の検証・ビジー化は行わない)。
    static State<Unit> LldtLtr((int mod, int reg, int rm) m) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from sel in GetMemOrRegData16(addr)
        from _ in SetCpu(cpu => { if (m.reg == 3) cpu.tr = sel; else cpu.ldtr = sel; return cpu; })
        select unit;

    static State<Unit> Group7_0F01 =>
        from _1 in SetLog("Group7_0F01")
        from m in ModRegRm()
        from _ in Group(
            m.reg,
            (0, Sgdt(m.mod, m.rm, idt: false)),
            (1, Sgdt(m.mod, m.rm, idt: true)),
            (2, Lgdt(m.mod, m.rm, idt: false)),
            (3, Lgdt(m.mod, m.rm, idt: true)),
            (7, Invlpg(m.mod, m.rm))
        )
        select unit;

    // INVLPG (0F 01 /7): 指定ページの TLB エントリを無効化する(簡便のため全フラッシュ)。
    // アドレスはデコードして消費するだけで、変換はしない。
    static State<Unit> Invlpg(int mod, int rm) =>
        from addr in GetMemOrRegOffset(mod, rm)
        from _ in Effect((env, _) => env.Paging.Flush())
        select unit;
}
