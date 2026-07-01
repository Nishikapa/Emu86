using static Emu86.CPU;
using static Emu86.Ext;
using static Emu86.Unit;
using static System.Console;

namespace Emu86;

struct OpecodeDic
{
    public State<Unit> state;
    public OpecodeDic[] next;
}

static class Program
{
    // JMP far ptr16:16 / ptr16:32 (0xEA)。
    // 実効オペランドサイズが32ビット(66プレフィックスまたは32ビットコード)なら offset は 32 ビット。
    // プロテクトモード中は CS にセレクタをロードし、32ビットコードセグメント(D=1)へのジャンプとみなす。
    static State<Unit> FarJump_EA =>
        from _1 in SetLog("FarJump_EA")
        from type in OperandType(true)
        from offset in (2 == type) ?
            GetMemoryDataIp32 :
            GetMemoryDataIp16.Select(dw => (uint)dw)
        from segment in GetMemoryDataIp16
        from _2 in _eip.Set(offset)
        from _3 in _cs.Set(segment)
        from _4 in SetCpu(cpu => { cpu.code32 = cpu.pe; return cpu; })
        select unit;

    static State<Unit> Jump_E9 =>
        from _1 in SetLog("Jump_E9")
        from type in OperandType(true)
        from inc in (2 == type) ?
            GetMemoryDataIp32.Select(dd => (int)dd) :
            GetMemoryDataIp16.Select(dw => (int)(short)dw)
        from _2 in IpInc(inc)
        select unit;

    // CALL far ptr16:16 (0x9A): CS, IP を push してから offset:segment へ far ジャンプ。
    static State<Unit> CallFar_9A =>
        from _1 in SetLog("CallFar_9A")
        from offset in GetMemoryDataIp16
        from segment in GetMemoryDataIp16
        from cs in GetSRegData(1)
        from _2 in Push16(cs)
        from ip in GetDataFromCpu(cpu => cpu.ip)
        from _3 in Push16(ip)
        from _4 in _ip.Set(offset)
        from _5 in _cs.Set(segment)
        select unit;

    // RETF (0xCB): IP, CS を pop して far 復帰する。
    static State<Unit> Retf_CB =>
        from _1 in SetLog("Retf_CB")
        from ip in Pop16
        from _2 in _ip.Set(ip)
        from cs in Pop16
        from _3 in _cs.Set(cs)
        select unit;

    // RETF imm16 (0xCA): IP, CS を pop して復帰後、SP += imm16。
    static State<Unit> Retf_CA =>
        from _1 in SetLog("Retf_CA")
        from imm in GetMemoryDataIp16
        from ip in Pop16
        from _2 in _ip.Set(ip)
        from cs in Pop16
        from _3 in _cs.Set(cs)
        from _4 in SetCpu(cpu => { cpu.sp += imm; return cpu; })
        select unit;

    static State<Unit> Group1_83 =>
        from _ in SetLog("Group1_83")
        from d1 in ModRegRm()
        from d2 in GetMemOrRegAddr(d1.mod, d1.rm)
        from d3 in GetMemoryDataIp8
        from d4 in GetMemOrRegData(d2, true)
        from d5 in Calc(d4.type, d4.db, d4.dw, d4.dd, d3, (ushort)(sbyte)d3, (uint)(sbyte)d3, d1.reg)
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
        var (ok, op, cpu2, log) = GetMemoryDataIp8(env, cpu1, ope);
        if (!ok)
            return (false, default, cpu1, log);

        // F3 90 = PAUSE(スピンループヒント)。実質 NOP として両バイトを消費する。
        if (op == 0x90)
            return (true, unit, cpu2, log);

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
        // LEA はメモリを読まず、実効アドレス(オフセット)そのものを reg へ書く。
        // GetMemOrRegAddr は物理アドレスを返すため、フラットモデル(code32)では実効アドレスと一致する。
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(true)
        from _2 in (2 == type) ?
            SetRegData32(m.reg, addr.addr) :
            SetRegData16(m.reg, (ushort)addr.addr)
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
                    d = 0x00000001; // FPU のみ(APIC/PAE/MSR 等は非対応として伏せる)
                    break;
                default:
                    a = b = c = d = 0;
                    break;
            }
            cpu.eax = a; cpu.ebx = b; cpu.ecx = c; cpu.edx = d;
            return cpu;
        })
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
        from _2 in type == 1 ? SetRegData16(m.reg, (ushort)prod) : SetRegData32(m.reg, (uint)prod)
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
        let bits = dst.type == 1 ? 16 : 32
        let dstv = dst.type == 1 ? (uint)dst.dw : dst.dd
        let srcv = dst.type == 1 ? (uint)src.dw : src.dd
        let count = cnt & 0x1F
        from _2 in count == 0
            ? unit.ToState()  // count==0 は結果・フラグとも不変
            : ApplyDoubleShift(addr, dst.type, DoubleShift(dstv, srcv, count, bits, left), bits)
        select unit;

    static State<Unit> ApplyDoubleShift(
        (bool isMem, uint addr) addr, int type, (uint result, bool cf, bool of) r, int bits) =>
        from _w in SetMemOrRegData(addr, type == 1 ? ((ushort)r.result).ToTypeData() : r.result.ToTypeData())
        from _f in SetCpu(
            (_cf, r.cf),
            (_of, r.of),
            (_zf, (r.result & (bits == 16 ? 0xFFFFu : 0xFFFFFFFFu)) == 0),
            (_sf, (r.result & (bits == 16 ? 0x8000u : 0x80000000u)) != 0)
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
        from offset in (2 == type) ?
            GetMemoryDataIp32.Select(dd => (int)dd) :
            GetMemoryDataIp16.Select(dw => (int)(short)dw)
        let inc = f ? offset : 0
        from _2 in IpInc(inc)
        select unit;

    // BT/BTS/BTR/BTC r/m, r (0F A3/AB/B3/BB): ビット番号はレジスタ reg。
    //   ope2 の bit3-4 で op を選ぶ: A3->BT(0) AB->BTS(1) B3->BTR(2) BB->BTC(3)
    static State<Unit> BitTest_reg =>
        from _1 in SetLog("BitTest_reg")
        from opecode in Opecodes
        let op = (opecode[1] >> 3) & 0x3
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, true)
        from bit in GetRegData16(m.reg)
        from _2 in BitTest(addr, data, bit, op)
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

    // BSF/BSR r16, r/m16 (0F BC/BD): ビットスキャン。BC=前方(最下位), BD=後方(最上位)。
    static State<Unit> BitScan_0FBC_BD =>
        from _1 in SetLog("BitScan_0FBC_BD")
        from opecode in Opecodes
        let forward = opecode[1] == 0xBC
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        from _2 in BitScan(m.reg, src.data.dw, forward)
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
        from _2 in (2 == type) ?
            SetRegData32(m.reg, value) :
            SetRegData16(m.reg, (ushort)value)
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
    static State<Unit> Group3_Test((int type, byte db, ushort dw, uint dd) data) =>
        from imm in GetMemoryDataIp_(data.type)
        from _ in update_eflags(data.type, (byte)(data.db & imm.db), (ushort)(data.dw & imm.dw), data.dd & imm.dd)
        select unit;

    // NOT r/m : ビット反転（フラグ変化なし）。
    static State<Unit> Group3_Not((bool isMem, uint addr) addr, (int type, byte db, ushort dw, uint dd) data) =>
        SetMemOrRegData(addr, data.MapType(b => (byte)~b, w => (ushort)~w, d => ~d));

    // NEG r/m : 0 - r/m。フラグは SUB と同じ。
    static State<Unit> Group3_Neg((bool isMem, uint addr) addr, (int type, byte db, ushort dw, uint dd) data) =>
        from _f in data.type == 0 ? update_eflags_sub((byte)0, data.db)
                 : data.type == 1 ? update_eflags_sub((ushort)0, data.dw)
                 : update_eflags_sub(0u, data.dd) // 注: byte/ushort の (byte)0/(ushort)0 はオーバーロード解決を明示するため残す
        from _w in SetMemOrRegData(addr, data.MapType(b => (byte)(0 - b), w => (ushort)(0 - w), d => 0u - d))
        select unit;

    // MUL r/m : 符号なし乗算。AL*r/m8->AX, AX*r/m16->DX:AX, EAX*r/m32->EDX:EAX。
    static State<Unit> Group3_Mul((int type, byte db, ushort dw, uint dd) data) =>
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
    static State<Unit> Group3_Imul((int type, byte db, ushort dw, uint dd) data) =>
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
        from _2 in type == 1 ? SetRegData16(m.reg, (ushort)prod) : SetRegData32(m.reg, (uint)prod)
        from _3 in SetCpu((_cf, of), (_of, of))
        select unit;

    // DIV r/m : 符号なし除算。商と剰余を AL/AH, AX/DX, EAX/EDX へ。
    static State<Unit> Group3_Div((int type, byte db, ushort dw, uint dd) data) =>
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
    static State<Unit> Group3_Idiv((int type, byte db, ushort dw, uint dd) data) =>
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
    static State<Unit> IncData((bool isMem, uint addr) addr, (int type, byte db, ushort dw, uint dd) data) =>
        from _f in data.type == 0 ? update_eflags_inc(data.db)
                 : data.type == 1 ? update_eflags_inc(data.dw)
                 : update_eflags_inc(data.dd)
        from _w in SetMemOrRegData(addr, data.MapType(b => (byte)(b + 1), w => (ushort)(w + 1), d => d + 1))
        select unit;

    // DEC r/m: type に応じて -1 し、フラグ(CF以外)を更新して書き戻す。
    static State<Unit> DecData((bool isMem, uint addr) addr, (int type, byte db, ushort dw, uint dd) data) =>
        from _f in data.type == 0 ? update_eflags_dec(data.db)
                 : data.type == 1 ? update_eflags_dec(data.dw)
                 : update_eflags_dec(data.dd)
        from _w in SetMemOrRegData(addr, data.MapType(b => (byte)(b - 1), w => (ushort)(w - 1), d => d - 1))
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
            (2, Call_rm(data)),         // 近傍間接 CALL
            (4, Jmp_rm(data)),          // 近傍間接 JMP
            (6, Push(data))             // PUSH r/m
        )
        select unit;

    static State<Unit> Jmp_rm((int type, byte db, ushort dw, uint dd) target) =>
        (2 == target.type) ? _eip.Set(target.dd) : _ip.Set(target.dw);

    // 近傍間接 CALL: 戻り番地(現在のIP/EIP)を push してから IP/EIP を target に設定する。
    static State<Unit> Call_rm((int type, byte db, ushort dw, uint dd) target) =>
        from cpu in GetCpu
        from _1 in Push((2 == target.type) ? cpu.eip.ToTypeData() : cpu.ip.ToTypeData())
        from _2 in Jmp_rm(target)
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

    static State<Unit> Cli_FA =>
        from _ in SetLog("Cli_FA")
        select unit;

    static State<Unit> Sti_FB =>
        from _ in SetLog("Sti_FB")
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
        from _2 in update_eflags(rmData.data.type,
            (byte)(rmData.data.db & regData.db), (ushort)(rmData.data.dw & regData.dw), rmData.data.dd & regData.dd)
        select unit;

    static State<Unit> Test_A8_A9 =>
        from _1 in SetLog("Test_A8_A9")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from type in OperandType(w)
        from acc in GetRegData(0, type) // 0=AL/AX/EAX
        from imm in GetMemoryDataIp_(type)
        from _2 in update_eflags(type, (byte)(acc.db & imm.db), (ushort)(acc.dw & imm.dw), acc.dd & imm.dd)
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
        from _2 in (2 == type) ? update_eflags_inc(v.dd) : update_eflags_inc(v.dw)
        from _3 in (2 == type) ?
            SetRegData32(reg, v.dd + 1) :
            SetRegData16(reg, (ushort)(v.dw + 1))
        select unit;

    static State<Unit> Dec_48_4F =>
        from _1 in SetLog("Dec_48_4F")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from v in GetRegData(reg, type)
        from _2 in (2 == type) ? update_eflags_dec(v.dd) : update_eflags_dec(v.dw)
        from _3 in (2 == type) ?
            SetRegData32(reg, v.dd - 1) :
            SetRegData16(reg, (ushort)(v.dw - 1))
        select unit;

    static State<Unit> Call_E8 =>
        from _1 in SetLog("Call_E8")
        from type in OperandType(true)
        from offset in (2 == type) ?
            GetMemoryDataIp32.Select(dd => (int)dd) :
            GetMemoryDataIp16.Select(dw => (int)(short)dw)
        // rel 読み取り後の IP/EIP（＝次命令アドレス）を戻り番地として push する。
        from cpu in GetCpu
        from _2 in Push((2 == type) ? cpu.eip.ToTypeData() : cpu.ip.ToTypeData())
        from _3 in IpInc(offset)
        select unit;

    static State<Unit> Ret_C3 =>
        from _1 in SetLog("Ret_C3")
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in (2 == type) ? _eip.Set(ret.dd) : _ip.Set(ret.dw)
        select unit;

    static State<Unit> Ret_C2 =>
        from _1 in SetLog("Ret_C2")
        from imm in GetMemoryDataIp16
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in (2 == type) ? _eip.Set(ret.dd) : _ip.Set(ret.dw)
        from _3 in SetCpu(cpu => { if (cpu.code32) { cpu.esp += imm; } else { cpu.sp += imm; } return cpu; })
        select unit;

    // PUSHA (0x60): AX,CX,DX,BX,(開始時SP),BP,SI,DI をこの順で push する。
    static State<Unit> Pusha_60 =>
        from _1 in SetLog("Pusha_60")
        from sp0 in GetRegData16(4) // 開始時点の SP を退避
        from _ax in GetRegData16(0)
        from p0 in Push16(_ax)
        from _cx in GetRegData16(1)
        from p1 in Push16(_cx)
        from _dx in GetRegData16(2)
        from p2 in Push16(_dx)
        from _bx in GetRegData16(3)
        from p3 in Push16(_bx)
        from p4 in Push16(sp0)
        from _bp in GetRegData16(5)
        from p5 in Push16(_bp)
        from _si in GetRegData16(6)
        from p6 in Push16(_si)
        from _di in GetRegData16(7)
        from p7 in Push16(_di)
        select unit;

    // POPA (0x61): DI,SI,BP,(SP読み飛ばし),BX,DX,CX,AX の順で pop する。SP は破棄。
    static State<Unit> Popa_61 =>
        from _1 in SetLog("Popa_61")
        from di in Pop16
        from _2 in SetRegData16(7, di)
        from si in Pop16
        from _3 in SetRegData16(6, si)
        from bp in Pop16
        from _4 in SetRegData16(5, bp)
        from skip in Pop16            // 元 SP は読み飛ばす
        from bx in Pop16
        from _5 in SetRegData16(3, bx)
        from dx in Pop16
        from _6 in SetRegData16(2, dx)
        from cx in Pop16
        from _7 in SetRegData16(1, cx)
        from ax in Pop16
        from _8 in SetRegData16(0, ax)
        select unit;

    // ENTER imm16, imm8 (0xC8): スタックフレームを構築する。
    static State<Unit> Enter_C8 =>
        from _1 in SetLog("Enter_C8")
        from alloc in GetMemoryDataIp16
        from level in GetMemoryDataIp8
        from _2 in Enter(alloc, level)
        select unit;

    // LEAVE (0xC9): SP <- BP; BP <- pop()。スタックフレームを破棄する。
    static State<Unit> Leave_C9 =>
        from _1 in SetLog("Leave_C9")
        from bp in GetRegData16(5)
        from _2 in _sp.Set(bp)        // SP <- BP
        from val in Pop16
        from _3 in SetRegData16(5, val) // BP <- [SP]
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

    static State<Unit> Pushf_9C =>
        from _1 in SetLog("Pushf_9C")
        from fl in GetDataFromCpu(cpu => (ushort)cpu.eflags)
        from _2 in Push16(fl)
        select unit;

    static State<Unit> Popf_9D =>
        from _1 in SetLog("Popf_9D")
        from fl in Pop16
        from _2 in SetCpu(cpu => { cpu.eflags = (cpu.eflags & 0xFFFF0000) | fl; return cpu; })
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

    // CBW (0x98): AL を符号拡張して AX にする。
    static State<Unit> Cbw_98 =>
        from _1 in SetLog("Cbw_98")
        from _2 in SetCpu(cpu => { cpu.ax = (ushort)(short)(sbyte)cpu.al; return cpu; })
        select unit;

    // CWD (0x99): AX を符号拡張して DX:AX にする（DX は符号ビットで埋める）。
    static State<Unit> Cwd_99 =>
        from _1 in SetLog("Cwd_99")
        from _2 in SetCpu(cpu => { cpu.dx = (ushort)((short)cpu.ax < 0 ? 0xFFFF : 0x0000); return cpu; })
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
            EnvOutPort(env, port, cpu.al);
            if (w) { EnvOutPort(env, port + 1, cpu.ah); }
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
            if (w) { cpu.ax = (ushort)(EnvInPort(env, port) | (EnvInPort(env, port + 1) << 8)); }
            else { cpu.al = EnvInPort(env, port); }
            return cpu;
        })
        select unit;

    // IN AL/AX, DX (0xEC/0xED)
    static State<Unit> In_EC_ED =>
        from _1 in SetLog("In_EC_ED")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        from _2 in SetCpu((env, cpu) =>
        {
            if (w) { cpu.ax = (ushort)(EnvInPort(env, cpu.dx) | (EnvInPort(env, cpu.dx + 1) << 8)); }
            else { cpu.al = EnvInPort(env, cpu.dx); }
            return cpu;
        })
        select unit;

    // OUT DX, AL/AX (0xEE/0xEF)
    static State<Unit> Out_EE_EF =>
        from _1 in SetLog("Out_EE_EF")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        from _2 in SetCpu((env, cpu) =>
        {
            EnvOutPort(env, cpu.dx, cpu.al);
            if (w) { EnvOutPort(env, cpu.dx + 1, cpu.ah); }
            return cpu;
        })
        select unit;

    static State<Unit> Lgdt(int mod, int rm) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from dw in GetMemOrRegData16(addr)
        from dd in GetMemOrRegData32((addr.isMem, addr.addr + 2))
        from _ in SetCpu(cpu => { cpu.gdt_base = dd; cpu.gdt_limit = dw; return cpu; })
        select unit;

    static State<Unit> Lidt(int mod, int rm) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from dw in GetMemOrRegData16(addr)
        from dd in GetMemOrRegData32((addr.isMem, addr.addr + 2))
        from _ in SetCpu(cpu => { cpu.idt_base = dd; cpu.idt_limit = dw; return cpu; })
        select unit;

    static State<Unit> Group7_0F01 =>
        from _1 in SetLog("Group7_0F01")
        from m in ModRegRm()
        from _ in Choice(
            m.reg,
            (2, Lgdt(m.mod, m.rm)),
            (3, Lidt(m.mod, m.rm))
        )
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

    static Dictionary<int, Accessor<CPU, bool>> PrefixStates =>
        new()
        {
            { 0x2E, _cs_prefix },
            { 0x26, _es_prefix },
            { 0x64, _fs_prefix },
            { 0x65, _gs_prefix },
            { 0x66, _operand_size_prefix },
            { 0x67, _address_size_prefix }
        };

    // 毎命令の再構築を避けるため、オペコードテーブルは static readonly でキャッシュする。
    static readonly OpecodeDic[] dicPrefixes =
        [.. Enumerable.Range(0, 256).Select(
            index => PrefixStates.TryGetValue(index, out var acc) ?
                new OpecodeDic() { state = acc.Set(true) } :
                default
        )];

    static State<Unit> ClearPrefixes =>
        PrefixStates.Values
        .Select(acc => acc.Set(false))
        .Sequence()
        .Ignore();

    static (byte ope, int len, State<Unit> state)[] OneByteStates =>
    [
        (0x00, 6, Arithmetic),
        (0x08, 6, Arithmetic),
        (0x10, 6, Arithmetic),
        (0x18, 6, Arithmetic),
        (0x20, 6, Arithmetic),
        (0x28, 6, Arithmetic),
        (0x30, 6, Arithmetic),
        (0x38, 6, Arithmetic),
        (0x40, 8, Inc_40_47),
        (0x48, 8, Dec_48_4F),
        (0x50, 8, Push_50_57),
        (0x58, 8, Pop_58_5F),
        (0x60, 1, Pusha_60),
        (0x61, 1, Popa_61),
        (0x6C, 2, Ins_6C_6D),
        (0x6E, 2, Outs_6E_6F),
        (0x68, 1, PushImm_68),
        (0x69, 1, Imul_69_6B),
        (0x6A, 1, PushImm_6A),
        (0x6B, 1, Imul_69_6B),
        (0x70, 16, Jcc_70_7F),
        (0x80, 2, Group1_80_81),
        (0x83, 1, Group1_83),
        (0x84, 2, Test_84_85),
        (0x88, 4, Mov_88_8B),
        (0x8C, 1, Mov_8C),
        (0x8D, 1, Lea_8D),
        (0x8E, 1, Mov_8E),
        (0x90, 1, Nop_90),
        (0x91, 7, Xchg_91_97),
        (0x9A, 1, CallFar_9A),
        (0x98, 1, Cbw_98),
        (0x99, 1, Cwd_99),
        (0x9C, 1, Pushf_9C),
        (0x9E, 1, Sahf_9E),
        (0x9F, 1, Lahf_9F),
        (0x9D, 1, Popf_9D),
        (0xA0, 2, Mov_A0_A1),
        (0xA2, 2, Mov_A2_A3),
        (0xA4, 2, Movs_A4_A5),
        (0xA6, 2, Cmps_A6_A7),
        (0xA8, 2, Test_A8_A9),
        (0xAA, 2, Stos_AA_AB),
        (0xAC, 2, Lods_AC_AD),
        (0xAE, 2, Scas_AE_AF),
        (0xB0, 16, Mov_B0_BF),
        (0xC0, 2, Group2_C0_C1),
        (0xC2, 1, Ret_C2),
        (0xC3, 1, Ret_C3),
        (0xC6, 2, Mov_C6_C7),
        (0xC8, 1, Enter_C8),
        (0xC9, 1, Leave_C9),
        (0xCA, 1, Retf_CA),
        (0xCB, 1, Retf_CB),
        (0xCC, 1, Int3_CC),
        (0xCD, 1, Int_CD),
        (0xCE, 1, Into_CE),
        (0xCF, 1, Iret_CF),
        (0xD0, 2, Group2_D0_D1),
        (0xD2, 2, Group2_D2_D3),
        (0xD7, 1, Xlat_D7),
        (0xE0, 3, Loop_E0_E2),
        (0xE3, 1, Jcxz_E3),
        (0xE4, 2, In_E4_E5),
        (0xE6, 2, Out_E6_E7),
        (0xE8, 1, Call_E8),
        (0xE9, 1, Jump_E9),
        (0xEA, 1, FarJump_EA),
        (0xEB, 1, Jmp_EB),
        (0xEC, 2, In_EC_ED),
        (0xEE, 2, Out_EE_EF),
        (0xF2, 1, Repne_F2),
        (0xF3, 1, Rep_F3),
        (0xF6, 2, Group3_F6_F7),
        (0xF4, 1, Hlt_F4),
        (0xF5, 1, Cmc_F5),
        (0xF8, 1, Clc_F8),
        (0xF9, 1, Stc_F9),
        (0xFA, 1, Cli_FA),
        (0xFB, 1, Sti_FB),
        (0xFC, 1, Cld_FC),
        (0xFD, 1, Std_FD),
        (0xFE, 1, Group4_FE),
        (0xFF, 1, Group5_FF),
    ];

    static (byte ope1, byte ope2, int len, State<Unit> state)[] TwoBytesStates =>
    [
        (0x0F, 0x80, 0x10, Jcc_0F80_0F8F),
        (0x0F, 0x01, 0x01, Group7_0F01),
        (0x0F, 0x20, 0x01, Mov_0F20),
        (0x0F, 0x22, 0x01, Mov_0F22),
        (0x0F, 0x90, 0x10, Setcc_0F90_9F),        // SETcc r/m8 (16 条件)
        (0x0F, 0xA3, 0x01, BitTest_reg),          // BT  r/m, r
        (0x0F, 0xAB, 0x01, BitTest_reg),          // BTS r/m, r
        (0x0F, 0xB3, 0x01, BitTest_reg),          // BTR r/m, r
        (0x0F, 0xBA, 0x01, Group8_0FBA),          // BT/BTS/BTR/BTC r/m, imm8
        (0x0F, 0xBB, 0x01, BitTest_reg),          // BTC r/m, r
        (0x0F, 0xB6, 0x02, MovzxMovsx_0FB6_BF),   // MOVZX r,r/m8 ; r,r/m16
        (0x0F, 0xBC, 0x02, BitScan_0FBC_BD),      // BSF/BSR r16, r/m16
        (0x0F, 0xBE, 0x02, MovzxMovsx_0FB6_BF),   // MOVSX r,r/m8 ; r,r/m16
        (0x0F, 0xA2, 0x01, Cpuid_0FA2),           // CPUID
        (0x0F, 0xA4, 0x02, ShldShrd),             // SHLD r/m,r,imm8 ; r/m,r,CL
        (0x0F, 0xAC, 0x02, ShldShrd),             // SHRD r/m,r,imm8 ; r/m,r,CL
        (0x0F, 0xAF, 0x01, Imul_0FAF)             // IMUL r, r/m
    ];

    static readonly Dictionary<int, State<Unit>> oneByte =
        OneByteStates
        .SelectMany(
            item =>
            Enumerable.Range(item.ope, item.len)
            .Select(ope => (ope, item.state))
        )
        .ToDictionary(
            item => item.ope,
            item => item.state
        );

    static readonly Dictionary<int, Dictionary<int, State<Unit>>> twoBytes =
        TwoBytesStates
        .ToLookup(item => item.ope1)
        .ToDictionary(
            item => (int)item.Key,
            item =>
                item.SelectMany(
                    item2 =>
                    Enumerable.Range(item2.ope2, item2.len)
                    .Select(ope => (ope, item2.state))
                )
                .ToDictionary(j => (int)j.ope, j => j.state)
            );

    static readonly OpecodeDic[] dic =
        [.. Enumerable.Range(0, 256).Select(
            index =>
                oneByte.TryGetValue(index, out var state) ? new OpecodeDic() { state = state } :
                twoBytes.TryGetValue(index, out var _dic) ? new OpecodeDic()
                {
                    next = Enumerable.Range(0, 256).Select(
                        index2 =>
                            _dic.TryGetValue(index2, out var state2) ?
                                new OpecodeDic() { state = state2 } :
                                default
                    ).ToArray()
                } : default
        )];

    static public State<Unit> CheckPrefix => (env, cpu1, ope) =>
    {
        var (IsSuccess1, op1, cpu2, log1) = GetMemoryDataIp8(env, cpu1, ope);

        if (!IsSuccess1)
        {
            return (false, default, cpu1, log1);
        }

        var data1 = dicPrefixes[op1];

        if (default != data1.state)
        {
            return data1.state(env, cpu2, [(byte)op1]);
        }

        return (false, default, cpu1, log1);
    };

    static public State<Unit> CheckPrefixes => CheckPrefix.Many0().Ignore();

    static public State<Unit> Execute => (env, cpu1, ope) =>
    {
        var (IsSuccess1, op1, cpu2, log1) = GetMemoryDataIp8(env, cpu1, ope);

        if (!IsSuccess1)
        {
            return (false, default, cpu1, log1);
        }

        var data1 = dic[op1];

        if (default != data1.state)
        {
            return data1.state(env, cpu2, [(byte)op1]);
        }

        if (default == data1.next)
        {
            return (false, default, cpu1, log1);
        }

        var (IsSuccess2, op2, cpu3, log2) = GetMemoryDataIp8(env, cpu2, ope);

        if (!IsSuccess2)
        {
            return (false, default, cpu1, log1);
        }

        var data2 = data1.next[op2];

        if (default != data2.state)
        {
            return data2.state(env, cpu3, [(byte)op1, (byte)op2]);
        }

        return (false, default, cpu1, log1 + log2);
    };

    static public State<Unit> Execute2 =>
        from _1 in CheckPrefixes
        from _2 in Execute
        from _3 in ClearPrefixes
        select unit;

    static void Main(string[] args)
    {
        var init_cpu1 = new CPU();
        var init_cpu2 = _ip.setter(init_cpu1)(0xFFF0);
        var init_cpu3 = _cs.setter(init_cpu2)(0xF000);

        var env = new EmuEnvironment();

        // 1命令ずつ実行し、trace.log に CS:EIP を記録する。
        // 未実装オペコードで停止したら、その地点とオペコードを表示する。
        var cpu = init_cpu3;
        var step = Execute2;
        long count = 0;
        using (var sw = new StreamWriter("trace.log"))
        {
            while (true)
            {
                var (ok, _, cpu2, log) = step(env, cpu, default);
                if (!ok)
                    break;
                cpu = cpu2;
                count++;
                sw.WriteLine($"{cpu.cs:x4}:{cpu.eip:x8}");
                if (5_000_000 <= count)
                {
                    WriteLine("instruction limit reached");
                    break;
                }
            }
        }

        var addr = GetCodeAddr(cpu).addr;
        WriteLine($"STOP at {cpu.cs:x4}:{cpu.eip:x8} after {count} instructions, opcode: " +
            string.Join(" ", env.OneMegaMemory_.Skip((int)addr).Take(6).Select(b => b.ToString("x2"))));
    }
}
