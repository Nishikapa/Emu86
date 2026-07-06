using static Emu86.CPU;
using static Emu86.Ext;
using static Emu86.Unit;

namespace Emu86;

static partial class Program
{
    // JMP far ptr16:16 / ptr16:32 (0xEA)。
    // 実効オペランドサイズが32ビット(66プレフィックスまたは32ビットコード)なら offset は 32 ビット。
    // CS のロードは LoadSReg 経由(プロテクトモードでは GDT 記述子から基底/D ビットを反映)。
    static State<Unit> FarJump_EA =>
        from _1 in SetLog("FarJump_EA")
        from type in OperandType(true)
        from offset in (2 == type) ?
            GetMemoryDataIp32 :
            GetMemoryDataIp16.Select(dw => (uint)dw)
        from segment in GetMemoryDataIp16
        from _2 in _eip.Set(offset)
        from _3 in LoadSReg(1, segment)
        select unit;

    // rel16/rel32 相対オフセットを実効オペランドサイズで読む(rel16 は符号拡張)。
    static State<int> JumpRel(int type) =>
        2 == type
            ? GetMemoryDataIp32.Select(dd => (int)dd)
            : GetMemoryDataIp16.Select(dw => (int)(short)dw);

    // 近傍ジャンプ先の設定(type に応じて IP / EIP)。
    static State<Unit> JmpTo(Data target) =>
        2 == target.type ? _eip.Set(target.dd) : _ip.Set(target.dw);

    static State<Unit> Jump_E9 =>
        from _1 in SetLog("Jump_E9")
        from type in OperandType(true)
        from inc in JumpRel(type)
        from _2 in IpInc(inc)
        select unit;

    // CALL far ptr16:16 / ptr16:32 (0x9A): CS, (E)IP を push してから offset:segment へ far ジャンプ。
    // オペランドサイズに従う(32ビットコードではオフセットは 32 ビット、リターンフレームも 32 ビット幅)。
    static State<Unit> CallFar_9A =>
        from _1 in SetLog("CallFar_9A")
        from type in OperandType(true)
        from offset in (2 == type) ? GetMemoryDataIp32 : GetMemoryDataIp16.Select(dw => (uint)dw)
        from segment in GetMemoryDataIp16
        from cs in GetSRegData(1)
        from _2 in (2 == type) ? Push(((uint)cs).ToTypeData()) : Push16(cs)
        from cpu in GetCpu
        from _3 in (2 == type) ? Push(cpu.eip.ToTypeData()) : Push16(cpu.ip)
        from _4 in (2 == type) ? _eip.Set(offset) : _ip.Set((ushort)offset)
        from _5 in LoadSReg(1, segment)
        select unit;

    // RETF (0xCB): (E)IP, CS を pop して far 復帰する(オペランドサイズに従う)。
    static State<Unit> Retf_CB =>
        from _1 in SetLog("Retf_CB")
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in (2 == type) ? _eip.Set(ret.dd) : _ip.Set(ret.dw)
        from cs in Pop(type)
        from _3 in LoadSReg(1, (ushort)cs.Value())
        select unit;

    // RETF imm16 (0xCA): (E)IP, CS を pop して復帰後、(E)SP += imm16。
    static State<Unit> Retf_CA =>
        from _1 in SetLog("Retf_CA")
        from imm in GetMemoryDataIp16
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in (2 == type) ? _eip.Set(ret.dd) : _ip.Set(ret.dw)
        from cs in Pop(type)
        from _3 in LoadSReg(1, (ushort)cs.Value())
        from _4 in SetCpu(cpu => { if (cpu.stack32) cpu.esp += imm; else cpu.sp += imm; return cpu; })
        select unit;

    // Group1 (0x83): r/m op imm8(符号拡張)
    static State<Unit> Group1_83 =>
        from _ in SetLog("Group1_83")
        from d1 in ModRegRm()
        from d2 in GetMemOrRegAddr(d1.mod, d1.rm)
        from d3 in GetMemoryDataIp8
        from d4 in GetMemOrRegData(d2, true)
        from d5 in Calc(d4, ((uint)(sbyte)d3).ToTypeData(d4.type), d1.reg)
        from d6 in SetMemOrRegData(d2, d5)
        select d6;

    static State<Unit> Movs_A4_A5 =>
        from _1 in SetLog("Movs_A4_A5")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Movs(w)
        select unit;

    static State<Unit> Stos_AA_AB =>
        from _1 in SetLog("Stos_AA_AB")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Stos(w)
        select unit;

    static State<Unit> Lods_AC_AD =>
        from _1 in SetLog("Lods_AC_AD")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Lods(w)
        select unit;

    static State<Unit> Cmps_A6_A7 =>
        from _1 in SetLog("Cmps_A6_A7")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Cmps(w)
        select unit;

    static State<Unit> Scas_AE_AF =>
        from _1 in SetLog("Scas_AE_AF")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Scas(w)
        select unit;

    static State<Unit> Ins_6C_6D =>
        from _1 in SetLog("Ins_6C_6D")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Ins(w)
        select unit;

    static State<Unit> Outs_6E_6F =>
        from _1 in SetLog("Outs_6E_6F")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Outs(w)
        select unit;

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
        // 対応する CPU フラグを立てる。例: F3 66 AB = REP STOSW(16ビット幅)。
        while (true)
        {
            var op0 = EnvGetMemoryData8(env, GetCodeAddr(cpu1).addr);
            if (default == dicPrefixes[op0].state) break;
            cpu1 = _eip.setter(cpu1)(cpu1.eip + 1);
            var (okp, _, cpup, _) = dicPrefixes[op0].state(env, cpu1, [op0]);
            if (!okp) return (false, default, cpu1, log: string.Empty);
            cpu1 = cpup;
        }

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
            if (!okB || dic[0x0F].next[op2].state == default)
                return (false, default, cpu1, log);
            return dic[0x0F].next[op2].state(env, cpuB, [0x0F, op2]);
        }

        if (!stringOps.Contains(op) || !oneByte.TryGetValue(op, out var state))
            return (false, default, cpu1, log);

        // 比較系のみ ZF 条件を適用する（MOVS/STOS/LODS は CX だけで回す）。
        var checkZf = repZf is bool && zfStringOps.Contains(op);

        var cpu = cpu2;
        // 32ビットアドレスサイズでは ECX、そうでなければ CX でカウントする。
        var a32 = cpu.code32 != cpu.address_size_prefix;
        while ((a32 ? cpu.ecx : cpu.cx) != 0)
        {
            var (ok2, _, cpuN, _) = state(env, cpu, [op]);
            if (!ok2)
                return (false, default, cpu1, log);
            cpu = a32 ? _ecx.setter(cpuN)(cpuN.ecx - 1) : _cx.setter(cpuN)((ushort)(cpuN.cx - 1));

            if (checkZf && _zf.getter(cpu) != repZf.Value)
                break;
        }
        return (true, unit, cpu, log);
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
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(w)
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
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from offset in MoffsOffset
        from type in OperandType(w)
        from _2 in MovAccFromMoffs(type, offset)
        select unit;

    // MOV [moffs], AL/AX/EAX (0xA2/0xA3): アキュムレータを直接アドレス(DS:offset)へ書き込む。
    static State<Unit> Mov_A2_A3 =>
        from _1 in SetLog("Mov_A2_A3")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from offset in MoffsOffset
        from type in OperandType(w)
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
        from _2 in SetResult(3 == m.mod)
        from data in GetCrReg(m.reg)
        from _3 in SetRegData32(m.rm, data)
        select unit;

    // MOV r32, DRx (0F 21) / MOV DRx, r32 (0F 23): デバッグレジスタ。
    // ハードウェアブレークポイントは未実装のため、値を保持するだけの汎用ストア。
    static State<Unit> Mov_0F21 =>
        from _1 in SetLog("Mov_0F21")
        from m in ModRegRm()
        from _2 in SetResult(3 == m.mod)
        from _3 in SetCpu((env, cpu) => EnvSetRegData32(env.Dr[m.reg])(m.rm)(cpu))
        select unit;

    static State<Unit> Mov_0F23 =>
        from _1 in SetLog("Mov_0F23")
        from m in ModRegRm()
        from _2 in SetResult(3 == m.mod)
        from data in GetRegData32(m.rm)
        from _3 in SetCpu((env, cpu) => { env.Dr[m.reg] = data; return cpu; })
        select unit;

    static State<Unit> Mov_0F22 =>
        from _1 in SetLog("Mov_0F22")
        from m in ModRegRm()
        from _2 in SetResult(3 == m.mod)
        from data in GetRegData32(m.rm)
        from _3 in SetCrReg(m.reg, data)
        select unit;

    // CPUID (0F A2): 入力 EAX=リーフ。最小限の機能のみ報告し、APIC 等の高度な
    // (未エミュレートの MMIO を要する)経路に SeaBIOS が入らないようにする。
    static State<Unit> Cpuid_0FA2 =>
        from _1 in SetLog("Cpuid_0FA2")
        from leaf in GetRegData32(0) // EAX
        from _2 in SetCpu(cpu =>
        {
            uint a, b, c, d;
            switch (leaf)
            {
                case 0: // 最大リーフ + ベンダ文字列 "GenuineIntel"
                    a = 1;
                    b = 0x756e6547; // "Genu"
                    d = 0x49656e69; // "ineI"
                    c = 0x6c65746e; // "ntel"
                    break;
                case 1: // バージョン情報と機能フラグ
                    a = 0x00000600; // family 6
                    b = 0;
                    c = 0;          // 拡張機能なし
                    // エミュレートできる機能のみ立てる:
                    //   FPU(0x1) PSE(0x8) TSC(0x10) MSR(0x20) PAE(0x40) CX8(0x100) CMOV(0x8000)
                    // 686-pae カーネルはここで PAE を確認して CR4.PAE を設定する。
                    // APIC/SEP/FXSR/MMX 等は未実装のため伏せる。
                    d = 0x00008179;
                    break;
                default:
                    a = b = c = d = 0;
                    break;
            }
            cpu.eax = a; cpu.ebx = b; cpu.ecx = c; cpu.edx = d;
            return cpu;
        })
        select unit;

    // RDTSC (0F 31): EDX:EAX <- タイムスタンプカウンタ。実行済み命令数を代用する
    // (Runner が毎命令 env.Tsc を更新する)。
    static State<Unit> Rdtsc_0F31 =>
        from _1 in SetLog("Rdtsc_0F31")
        from _2 in SetCpu((env, cpu) =>
        {
            cpu.eax = (uint)env.Tsc;
            cpu.edx = (uint)(env.Tsc >> 32);
            return cpu;
        })
        select unit;

    // RDMSR (0F 32) / WRMSR (0F 30): ECX で選択した MSR を EDX:EAX で読み書きする。
    // 実際の機能は持たない汎用ストア(未書き込みの MSR は 0 を返す)。
    static State<Unit> Rdmsr_0F32 =>
        from _1 in SetLog("Rdmsr_0F32")
        from _2 in SetCpu((env, cpu) =>
        {
            var v = env.Msrs.GetValueOrDefault(cpu.ecx);
            cpu.eax = (uint)v;
            cpu.edx = (uint)(v >> 32);
            return cpu;
        })
        select unit;

    static State<Unit> Wrmsr_0F30 =>
        from _1 in SetLog("Wrmsr_0F30")
        from _2 in SetCpu((env, cpu) =>
        {
            env.Msrs[cpu.ecx] = ((ulong)cpu.edx << 32) | cpu.eax;
            return cpu;
        })
        select unit;

    // CMPXCHG r/m, r (0F B0/B1): acc(AL/AX/EAX) と r/m を比較し、
    // 一致なら r/m <- reg、不一致なら acc <- r/m。フラグは CMP(acc, r/m) と同じ。
    static State<Unit> Cmpxchg_0FB0_B1 =>
        from _1 in SetLog("Cmpxchg_0FB0_B1")
        from opecode in Opecodes
        let w = 0 != (opecode[1] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from dst in GetMemOrRegData(addr, w)
        from acc in GetRegData(0, dst.type)
        from _f in Calc(acc, dst, 7) // CMP acc, r/m のフラグ
        from _2 in acc.Value() == dst.Value()
            ? from src in GetRegData(m.reg, dst.type)
              from _w in SetMemOrRegData(addr, src)
              select unit
            : SetRegData(0, dst)
        select unit;

    // XADD r/m, r (0F C0/C1): temp = r/m + reg(フラグは ADD)、reg <- r/m 旧値、r/m <- temp。
    static State<Unit> Xadd_0FC0_C1 =>
        from _1 in SetLog("Xadd_0FC0_C1")
        from opecode in Opecodes
        let w = 0 != (opecode[1] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from dst in GetMemOrRegData(addr, w)
        from src in GetRegData(m.reg, dst.type)
        from sum in Calc(dst, src, 0)
        from _2 in SetRegData(m.reg, dst)
        from _3 in SetMemOrRegData(addr, sum)
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
        from _2 in SetResult(m.reg == 1 && m.mod != 3)
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

    // IMUL r, r/m (0F AF): 2オペランド符号付き乗算 r = r * r/m。
    static State<Unit> Imul_0FAF =>
        from _1 in SetLog("Imul_0FAF")
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        let type = src.data.type
        from dst in GetRegData(m.reg, type)
        let a = type == 1 ? (short)dst.dw : (int)dst.dd
        let b = type == 1 ? (short)src.data.dw : (int)src.data.dd
        let prod = (long)a * b
        let of = type == 1 ? prod < short.MinValue || prod > short.MaxValue
                           : prod < int.MinValue || prod > int.MaxValue
        from _2 in SetRegData(m.reg, ((uint)prod).ToTypeData(type))
        from _3 in SetCpu((_cf, of), (_of, of))
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
        from _2 in count == 0
            ? unit.ToState()  // count==0 は結果・フラグとも不変
            : ApplyDoubleShift(addr, dst.type, DoubleShift(dst.Value(), src.Value(), count, Bits(dst.type), left))
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

    static State<Unit> Jcc_0F80_0F8F =>
        from _1 in SetLog("Jcc_0F80_0F8F")
        from opecode in Opecodes
        let cond = opecode[1] & 0xF
        from f in Jcc(cond)
        from type in OperandType(true)
        from offset in JumpRel(type)
        let inc = f ? offset : 0
        from _2 in IpInc(inc)
        select unit;

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
        let bitRaw = type == 1 ? (short)bitData.dw : (int)bitData.dd
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
        from _2 in f ? SetRegData(m.reg, src.data) : unit.ToState()
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

    static State<Unit> Mov_88_8B =>
        from _1 in SetLog("Mov_88_8B")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        let d = 0 != (opecode[0] & 0x02)
        from m in ModRegRm()
        from rmData in GetMemOrRegData(m.mod, m.rm, w)
        from regData in GetRegData(m.reg, rmData.data.type)
        // d=1: reg <- r/m (0x8A/0x8B) , d=0: r/m <- reg (0x88/0x89)
        from _2 in d ? SetRegData(m.reg, rmData.data) : SetMemOrRegData(rmData.input, regData)
        select unit;

    static State<Unit> Arithmetic =>
        from _1 in SetLog("Arithmetic")
        from opecode in Opecodes
        let kind = (opecode[0] >> 3) & 0x7
        let form = opecode[0] & 0x7
        // 下位3ビットが 4/5 のものは ModRM を持たない AL/AX/EAX, imm 形式。
        from _2 in (form < 4) ? ArithmeticModRM(kind) : ArithmeticAccImm(kind, 0 != (form & 1))
        select unit;

    static State<Unit> ArithmeticModRM(int kind) =>
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        let d = 0 != (opecode[0] & 2)
        from m in ModRegRm()
        from rmData in GetMemOrRegData(m.mod, m.rm, w)
        from regData in GetRegData(m.reg, rmData.data.type)
        let d1 = d ? regData : rmData.data
        let d2 = d ? rmData.data : regData
        from ret in Calc(d1, d2, kind)
        from _2 in d ? SetRegData(m.reg, ret) : SetMemOrRegData(rmData.input, ret)
        select unit;

    static State<Unit> ArithmeticAccImm(int kind, bool w) =>
        from type in OperandType(w)
        from imm in GetMemoryDataIp_(type)
        from acc in GetRegData(0, type)
        from ret in Calc(acc, imm, kind)
        from _ in SetRegData(0, ret)
        select unit;

    // Group2 (0xD0/0xD1): r/m, 1
    static State<Unit> Group2_D0_D1 =>
        from _1 in SetLog("Group2_D0_D1")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, w)
        from ret in Group2(data, 1, m.reg)
        from _2 in SetMemOrRegData(addr, ret)
        select unit;

    // Group2 (0xD2/0xD3): r/m, CL
    static State<Unit> Group2_D2_D3 =>
        from _1 in SetLog("Group2_D2_D3")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, w)
        from cl in GetRegData(1, 0) // CL = reg8 #1
        from ret in Group2(data, cl.db, m.reg)
        from _2 in SetMemOrRegData(addr, ret)
        select unit;

    // Group2 (0xC0/0xC1): r/m, imm8
    static State<Unit> Group2_C0_C1 =>
        from _1 in SetLog("Group2_C0_C1")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, w)
        from imm in GetMemoryDataIp8
        from ret in Group2(data, imm, m.reg)
        from _2 in SetMemOrRegData(addr, ret)
        select unit;

    // Group3 (0xF6/0xF7): reg で TEST/NOT/NEG/MUL/IMUL/DIV/IDIV を選ぶ。
    static State<Unit> Group3_F6_F7 =>
        from _1 in SetLog("Group3_F6_F7")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, w)
        from _2 in Choice(
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
        from _ in update_eflags((data.Value() & imm.Value()).ToTypeData(data.type))
        select unit;

    // NOT r/m : ビット反転（フラグ変化なし）。
    static State<Unit> Group3_Not(MemAddr addr, Data data) =>
        SetMemOrRegData(addr, data.MapType(b => (byte)~b, w => (ushort)~w, d => ~d));

    // NEG r/m : 0 - r/m。フラグは SUB と同じ。
    static State<Unit> Group3_Neg(MemAddr addr, Data data) =>
        from _f in data.type == 0 ? update_eflags_sub((byte)0, data.db)
                 : data.type == 1 ? update_eflags_sub((ushort)0, data.dw)
                 : update_eflags_sub(0u, data.dd) // 注: byte/ushort の (byte)0/(ushort)0 はオーバーロード解決を明示するため残す
        from _w in SetMemOrRegData(addr, data.MapType(b => (byte)(0 - b), w => (ushort)(0 - w), d => 0u - d))
        select unit;

    // MUL r/m : 符号なし乗算。AL*r/m8->AX, AX*r/m16->DX:AX, EAX*r/m32->EDX:EAX。
    static State<Unit> Group3_Mul(Data data) =>
        from cpu in GetCpu
        let upper =
            data.type == 0 ? (uint)((cpu.al * data.db) >> 8) & 0xFF :
            data.type == 1 ? ((uint)cpu.ax * data.dw >> 16) & 0xFFFF :
                             (uint)((ulong)cpu.eax * data.dd >> 32)
        from _1 in SetCpu(c =>
        {
            if (data.type == 0) { c.ax = (ushort)(c.al * data.db); }
            else if (data.type == 1) { uint p = (uint)c.ax * data.dw; c.ax = (ushort)p; c.dx = (ushort)(p >> 16); }
            else { ulong p = (ulong)c.eax * data.dd; c.eax = (uint)p; c.edx = (uint)(p >> 32); }
            return c;
        })
        from _2 in SetCpu((_cf, upper != 0), (_of, upper != 0))
        select unit;

    // IMUL r/m : 符号付き乗算。
    static State<Unit> Group3_Imul(Data data) =>
        from cpu in GetCpu
        let prod = data.type == 0 ? (sbyte)cpu.al * (sbyte)data.db
                 : data.type == 1 ? (short)cpu.ax * (short)data.dw
                 : (long)(int)cpu.eax * (int)data.dd
        let of = data.type == 0 ? prod < sbyte.MinValue || prod > sbyte.MaxValue
               : data.type == 1 ? prod < short.MinValue || prod > short.MaxValue
               : prod < int.MinValue || prod > int.MaxValue
        from _1 in SetCpu(c =>
        {
            if (data.type == 0) { c.ax = (ushort)(short)prod; }
            else if (data.type == 1) { c.ax = (ushort)prod; c.dx = (ushort)(prod >> 16); }
            else { c.eax = (uint)prod; c.edx = (uint)(prod >> 32); }
            return c;
        })
        from _2 in SetCpu((_cf, of), (_of, of))
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
        from imm in imm8
            ? GetMemoryDataIp8.Select(b => (long)(sbyte)b)
            : GetMemoryDataIp_(type).Select(d => type == 1 ? (long)(short)d.dw : (long)(int)d.dd)
        let a = type == 1 ? (long)(short)src.data.dw : (long)(int)src.data.dd
        let prod = a * imm
        let of = type == 1
            ? prod < short.MinValue || prod > short.MaxValue
            : prod < int.MinValue || prod > int.MaxValue
        from _2 in SetRegData(m.reg, ((uint)prod).ToTypeData(type))
        from _3 in SetCpu((_cf, of), (_of, of))
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
        from _w in SetMemOrRegData(addr, (data.Value() + 1).ToTypeData(data.type))
        select unit;

    // DEC r/m: type に応じて -1 し、フラグ(CF以外)を更新して書き戻す。
    static State<Unit> DecData(MemAddr addr, Data data) =>
        from _f in update_eflags_dec(data)
        from _w in SetMemOrRegData(addr, (data.Value() - 1).ToTypeData(data.type))
        select unit;

    // Group4 (0xFE): reg=0 INC r/m8, reg=1 DEC r/m8
    static State<Unit> Group4_FE =>
        from _1 in SetLog("Group4_FE")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, false)
        from _2 in Choice(
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
        from _2 in Choice(
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
    // CS のロードは LoadSReg 経由(プロテクトモードでは GDT 記述子から基底/D ビットを反映)。
    static State<Unit> JmpFar_rm(MemAddr addr, int type) =>
        from offset in type == 2 ? GetMemOrRegData32(addr) : GetMemOrRegData16(addr).Select(dw => (uint)dw)
        from sel in GetMemOrRegData16((addr.isMem, addr.addr + (uint)(type == 2 ? 4 : 2)))
        from _1 in _eip.Set(offset)
        from _2 in LoadSReg(1, sel)
        select unit;

    // far 間接 CALL: CS と IP/EIP をオペランドサイズ幅で push してから far ジャンプする。
    static State<Unit> CallFar_rm(MemAddr addr, int type) =>
        from cs in GetSRegData(1)
        from _1 in type == 2 ? Push(((uint)cs).ToTypeData()) : Push16(cs)
        from cpu in GetCpu
        from _2 in type == 2 ? Push(cpu.eip.ToTypeData()) : Push16(cpu.ip)
        from _3 in JmpFar_rm(addr, type)
        select unit;

    // 近傍間接 CALL: 戻り番地(現在のIP/EIP)を push してから IP/EIP を target に設定する。
    static State<Unit> Call_rm(Data target) =>
        from cpu in GetCpu
        from _1 in Push((2 == target.type) ? cpu.eip.ToTypeData() : cpu.ip.ToTypeData())
        from _2 in JmpTo(target)
        select unit;

    // WAIT/FWAIT (0x9B): FPU 例外を同期する。例外は配送しないため NOP。
    static State<Unit> Fwait_9B =>
        from _ in SetLog("Fwait_9B")
        select unit;

    // INT imm8 (0xCD): 指定ベクタへソフトウェア割り込み。
    static State<Unit> Int_CD =>
        from _1 in SetLog("Int_CD")
        from vector in GetMemoryDataIp8
        from _2 in Interrupt(vector)
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
        from _2 in of ? Interrupt(4) : unit.ToState()
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

    static State<Unit> Test_84_85 =>
        from _1 in SetLog("Test_84_85")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from rmData in GetMemOrRegData(m.mod, m.rm, w)
        from regData in GetRegData(m.reg, rmData.data.type)
        // TEST は AND の結果を捨ててフラグ(CF=OF=0, ZF/SF)だけ更新する。
        from _2 in update_eflags((rmData.data.Value() & regData.Value()).ToTypeData(rmData.data.type))
        select unit;

    static State<Unit> Test_A8_A9 =>
        from _1 in SetLog("Test_A8_A9")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from type in OperandType(w)
        from acc in GetRegData(0, type) // 0=AL/AX/EAX
        from imm in GetMemoryDataIp_(type)
        from _2 in update_eflags((acc.Value() & imm.Value()).ToTypeData(type))
        select unit;

    // XCHG r/m, r (0x86/0x87)
    static State<Unit> Xchg_86_87 =>
        from _1 in SetLog("Xchg_86_87")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from rmData in GetMemOrRegData(m.mod, m.rm, w)
        from regData in GetRegData(m.reg, rmData.data.type)
        from _2 in SetMemOrRegData(rmData.input, regData)
        from _3 in SetRegData(m.reg, rmData.data)
        select unit;

    static State<Unit> Xchg_91_97 =>
        from _1 in SetLog("Xchg_91_97")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from ax in GetRegData(0, type)
        from rv in GetRegData(reg, type)
        from _2 in SetRegData(0, rv)
        from _3 in SetRegData(reg, ax)
        select unit;

    static State<Unit> Inc_40_47 =>
        from _1 in SetLog("Inc_40_47")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from v in GetRegData(reg, type)
        from _2 in IncData((false, (uint)reg), v)
        select unit;

    static State<Unit> Dec_48_4F =>
        from _1 in SetLog("Dec_48_4F")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from v in GetRegData(reg, type)
        from _2 in DecData((false, (uint)reg), v)
        select unit;

    static State<Unit> Call_E8 =>
        from _1 in SetLog("Call_E8")
        from type in OperandType(true)
        from offset in JumpRel(type)
        // rel 読み取り後の IP/EIP（＝次命令アドレス）を戻り番地として push する。
        from cpu in GetCpu
        from _2 in Push((2 == type) ? cpu.eip.ToTypeData() : cpu.ip.ToTypeData())
        from _3 in IpInc(offset)
        select unit;

    static State<Unit> Ret_C3 =>
        from _1 in SetLog("Ret_C3")
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in JmpTo(ret)
        select unit;

    static State<Unit> Ret_C2 =>
        from _1 in SetLog("Ret_C2")
        from imm in GetMemoryDataIp16
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in JmpTo(ret)
        from _3 in SetCpu(cpu => { if (cpu.stack32) { cpu.esp += imm; } else { cpu.sp += imm; } return cpu; })
        select unit;

    // PUSHA (0x60): AX,CX,DX,BX,(開始時SP),BP,SI,DI をこの順で push する。
    static State<Unit> Pusha_60 =>
        from _1 in SetLog("Pusha_60")
        from sp0 in GetRegData16(4) // 開始時点の SP を退避(reg=4 はこの値を push する)
        from _2 in Enumerable.Range(0, 8)
            .Select(reg =>
                from v in reg == 4 ? sp0.ToState() : GetRegData16(reg)
                from p in Push16(v)
                select unit)
            .Sequence()
            .Ignore()
        select unit;

    // POPA (0x61): DI,SI,BP,(SP読み飛ばし),BX,DX,CX,AX の順で pop する。SP は破棄。
    static State<Unit> Popa_61 =>
        from _1 in SetLog("Popa_61")
        from _2 in new[] { 7, 6, 5, 4, 3, 2, 1, 0 }
            .Select(reg =>
                from v in Pop16
                // reg=4(元 SP)は読み飛ばして破棄する
                from _ in reg == 4 ? unit.ToState() : SetRegData16(reg, v)
                select unit)
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
        from _2 in pop
            ? from v in Pop(type)
              from _w in LoadSReg(reg, (ushort)v.Value())
              select unit
            : from v in GetSRegData(reg)
              from _w in Push(type == 2 ? ((uint)v).ToTypeData() : v.ToTypeData())
              select unit
        select unit;

    // PUSH Sreg (06/0E/16/1E): opcode>>3 が ES/CS/SS/DS の順のセグメント番号になる。
    // 32ビットモードでは 4 バイト push する(セレクタをゼロ拡張)。オペランドサイズに従う。
    static State<Unit> PushSreg =>
        from _1 in SetLog("PushSreg")
        from opecode in Opecodes
        let reg = opecode[0] >> 3
        from type in OperandType(true)
        from v in GetSRegData(reg)
        from _2 in Push(type == 2 ? ((uint)v).ToTypeData() : v.ToTypeData())
        select unit;

    // POP Sreg (07/17/1F)。オペランドサイズ幅で pop し、下位16ビットをロードする。
    // プロテクトモードでは記述子を再ロードする。
    static State<Unit> PopSreg =>
        from _1 in SetLog("PopSreg")
        from opecode in Opecodes
        let reg = opecode[0] >> 3
        from type in OperandType(true)
        from v in Pop(type)
        from _2 in LoadSReg(reg, (ushort)v.Value())
        select unit;

    static State<Unit> Push_50_57 =>
        from _1 in SetLog("Push_50_57")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from value in GetRegData(reg, type)
        from _2 in Push(value)
        select unit;

    static State<Unit> Pop_58_5F =>
        from _1 in SetLog("Pop_58_5F")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from value in Pop(type)
        from _2 in SetRegData(reg, value)
        select unit;

    // PUSH imm16/imm32 (0x68): オペランドサイズで imm を読み、その幅で push する。
    // POP r/m (0x8F /0)
    static State<Unit> Pop_8F =>
        from _1 in SetLog("Pop_8F")
        from m in ModRegRm()
        from _2 in SetResult(0 == m.reg)
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(true)
        from data in Pop(type)
        from _3 in SetMemOrRegData(addr, data)
        select unit;

    static State<Unit> PushImm_68 =>
        from _1 in SetLog("PushImm_68")
        from type in OperandType(true)
        from value in GetMemoryDataIp_(type)
        from _2 in Push(value)
        select unit;

    // PUSH imm8 (0x6A): imm8 を符号拡張し、オペランドサイズ幅で push する。
    static State<Unit> PushImm_6A =>
        from _1 in SetLog("PushImm_6A")
        from imm in GetMemoryDataIp8
        from type in OperandType(true)
        from _2 in Push(type == 2
            ? ((uint)(sbyte)imm).ToTypeData()
            : ((ushort)(sbyte)imm).ToTypeData())
        select unit;

    // PUSHF/POPF はオペランドサイズに従う(32ビットコードでは EFLAGS 全体を 4 バイトで push/pop)。
    static State<Unit> Pushf_9C =>
        from _1 in SetLog("Pushf_9C")
        from type in OperandType(true)
        from fl in GetDataFromCpu(cpu => cpu.eflags)
        from _2 in Push(type == 2 ? fl.ToTypeData() : ((ushort)fl).ToTypeData())
        select unit;

    static State<Unit> Popf_9D =>
        from _1 in SetLog("Popf_9D")
        from type in OperandType(true)
        from fl in Pop(type)
        from _2 in SetCpu(cpu => { cpu.eflags = type == 2 ? fl.dd : (cpu.eflags & 0xFFFF0000) | fl.dw; return cpu; })
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

    static State<Unit> Nop_90 =>
        from _ in SetLog("Nop_90")
        select unit;

    static State<Unit> Hlt_F4 =>
        from _ in SetLog("Hlt_F4")
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
        from _2 in SetCpu(cpu => { cpu.cf = !cpu.cf; return cpu; })
        select unit;

    // LOOP系 (0xE0-0xE2): CX-- してから、条件を満たせば rel8 分岐する。
    //   0xE2 LOOP   : CX!=0
    //   0xE1 LOOPE  : CX!=0 && ZF==1
    //   0xE0 LOOPNE : CX!=0 && ZF==0
    static State<Unit> Loop_E0_E2 =>
        from _1 in SetLog("Loop_E0_E2")
        from opecode in Opecodes
        from offset in GetMemoryDataIp8
        from cx in GetRegData16(1)
        let newCx = (ushort)(cx - 1)
        from _2 in SetRegData16(1, newCx)
        from zf in GetDataFromCpu(cpu => cpu.zf)
        let cond = (opecode[0] & 0x03) switch
        {
            0 => newCx != 0 && !zf,   // LOOPNE
            1 => newCx != 0 && zf,    // LOOPE
            _ => newCx != 0,          // LOOP
        }
        from _3 in IpInc(cond ? (sbyte)offset : 0)
        select unit;

    // JCXZ (0xE3): CX==0 なら rel8 分岐（CX は変更しない）。
    static State<Unit> Jcxz_E3 =>
        from _1 in SetLog("Jcxz_E3")
        from offset in GetMemoryDataIp8
        from cx in GetRegData16(1)
        from _2 in IpInc(cx == 0 ? (sbyte)offset : 0)
        select unit;

    static State<Unit> Jmp_EB =>
        from _1 in SetLog("Jmp_EB")
        from offset in GetMemoryDataIp8
        from _2 in IpInc((sbyte)offset)
        select unit;

    static State<Unit> Jcc_70_7F =>
        from _1 in SetLog("Jcc_70_7F")
        from opecode in Opecodes
        let type = opecode[0] & 0xF
        from f in Jcc(type)
        from offset in GetMemoryDataIp8
        let inc = f ? (sbyte)offset : 0
        from _2 in IpInc(inc)
        select unit;

    static State<Unit> Out_E6_E7 =>
        from _1 in SetLog("Out_E6_E7")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        from port in GetMemoryDataIp8
        from _2 in SetCpu((env, cpu) =>
        {
            EnvOutPortN(env, port, w ? 2 : 1, w ? cpu.ax : cpu.al);
            return cpu;
        })
        select unit;

    static State<Unit> In_E4_E5 =>
        from _1 in SetLog("In_E4_E5")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        from port in GetMemoryDataIp8
        from _2 in SetCpu((env, cpu) =>
        {
            if (w) { cpu.ax = (ushort)EnvInPortN(env, port, 2); }
            else { cpu.al = (byte)EnvInPortN(env, port, 1); }
            return cpu;
        })
        select unit;

    // IN AL/AX/EAX, DX (0xEC/0xED)
    static State<Unit> In_EC_ED =>
        from _1 in SetLog("In_EC_ED")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        from type in OperandType(w)
        from _2 in SetCpu((env, cpu) =>
        {
            var v = EnvInPortN(env, cpu.dx, Bits(type) / 8);
            if (type == 0) { cpu.al = (byte)v; }
            else if (type == 1) { cpu.ax = (ushort)v; }
            else { cpu.eax = v; }
            return cpu;
        })
        select unit;

    // OUT DX, AL/AX/EAX (0xEE/0xEF)
    static State<Unit> Out_EE_EF =>
        from _1 in SetLog("Out_EE_EF")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        from type in OperandType(w)
        from _2 in SetCpu((env, cpu) =>
        {
            EnvOutPortN(env, cpu.dx, Bits(type) / 8, type == 0 ? cpu.al : type == 1 ? cpu.ax : cpu.eax);
            return cpu;
        })
        select unit;

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

    // Group6 (0F 00): SLDT/STR/LLDT/LTR。LDT・タスクスイッチは未使用のため、
    // LLDT/LTR はセレクタを読み捨て、SLDT/STR は 0 を返す最小実装(CPU 側に状態は持たない)。
    static State<Unit> Group6_0F00 =>
        from _1 in SetLog("Group6_0F00")
        from m in ModRegRm()
        from _ in Choice(
            m.reg,
            (0, SldtStr(m)),   // SLDT
            (1, SldtStr(m)),   // STR
            (2, LldtLtr(m)),   // LLDT
            (3, LldtLtr(m))    // LTR
        )
        select unit;

    static State<Unit> SldtStr((int mod, int reg, int rm) m) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from _ in SetMemOrRegData(addr, ((ushort)0).ToTypeData())
        select unit;

    static State<Unit> LldtLtr((int mod, int reg, int rm) m) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from _ in GetMemOrRegData16(addr) // セレクタを読み捨てる
        select unit;

    static State<Unit> Group7_0F01 =>
        from _1 in SetLog("Group7_0F01")
        from m in ModRegRm()
        from _ in Choice(
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
        from _ in SetCpu((env, cpu) => { env.FlushTlb(); return cpu; })
        select unit;

    static State<Unit> Group1_80_81 =>
        from _1 in SetLog("Group1_80_81")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from data1 in GetMemOrRegData(m.mod, m.rm, w)
        from data2 in GetMemoryDataIp_(data1.data.type)
        from ret in Calc(data1.data, data2, m.reg)
        from _ in SetMemOrRegData(data1.input, ret)
        select unit;
}
