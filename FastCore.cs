namespace Emu86;

// 方針B: 命令的な高速実行コア。
// モナド版(Program.Execute2)と 1 命令単位で完全に同じ状態遷移を行う「速い写像」であり、
// 意味論の正は常にモナド版に置く(分岐トレースのバイト一致で回帰検証する)。
// そのため x86 の仕様に照らして不正確な挙動(PF/AF を更新しない、PUSHF/LEAVE が 16bit 固定、
// JL/JLE の条件式など)も、意図的にモナド版と同一にしてある。
// 未対応の命令は CPU を一切変更せずに false を返し、呼び出し側がモナド版へフォールバックする。
static public partial class Ext
{
    // 1 命令を実行できたら true(cpu は更新済み)。対応外なら cpu 無変更で false。
    // デコード中はローカル変数だけを使い、コミット(cpu への書き込み)は実行が確定してから行う。
    static public bool FastStep(EmuEnvironment env, CPU cpu)
    {
        var startEip = cpu.eip;
        var code32 = cpu.code32;
        var csBase = cpu.cs_base;
        uint len = 0;

        // コードフェッチ。16 ビットコードでは IP(下位 16 ビット)でラップする(モナド版と同一)。
        uint FetchAddr() => csBase + (code32 ? startEip + len : (ushort)(startEip + len));
        byte F8() { var v = EnvGetMemoryData8(env, FetchAddr()); len += 1; return v; }
        ushort F16() { var v = EnvGetMemoryData16(env, FetchAddr()); len += 2; return v; }
        uint F32() { var v = EnvGetMemoryData32(env, FetchAddr()); len += 4; return v; }
        uint FImm(int type) => type == 0 ? F8() : type == 1 ? F16() : F32();

        // プレフィックス(モナド版と同じく、複数のセグメントオーバーライドは ES>CS>SS>DS>FS>GS の優先順)。
        bool p66 = false, p67 = false, pes = false, pcs = false, pss = false, pds = false, pfs = false, pgs = false;
        while (true)
        {
            var b = EnvGetMemoryData8(env, FetchAddr());
            if (b == 0x66) p66 = true;
            else if (b == 0x67) p67 = true;
            else if (b == 0x26) pes = true;
            else if (b == 0x2E) pcs = true;
            else if (b == 0x36) pss = true;
            else if (b == 0x3E) pds = true;
            else if (b == 0x64) pfs = true;
            else if (b == 0x65) pgs = true;
            else break;
            len++;
        }

        var typeW = (code32 != p66) ? 2 : 1;  // w=1 の実効オペランドサイズ(1=word, 2=dword)
        var a32 = code32 != p67;              // 実効アドレスサイズ

        uint? ovr =
            pes ? cpu.es_base :
            pcs ? cpu.cs_base :
            pss ? cpu.ss_base :
            pds ? cpu.ds_base :
            pfs ? cpu.fs_base :
            pgs ? cpu.gs_base : null;
        var segBase = ovr ?? cpu.ds_base;
        var ssSegBase = ovr ?? cpu.ss_base;

        // ModRM(+SIB+disp)を読み、(メモリか, 実効オフセット, セグメント基底, reg フィールド) を返す。
        // mod=3 のときオフセットはレジスタ番号。式の型と演算順はモナド版の実装と揃えてある。
        (bool isMem, uint off, uint segB, int reg) ReadModRM()
        {
            var modrm = F8();
            var mod = (modrm >> 6) & 3;
            var reg = (modrm >> 3) & 7;
            var rm = modrm & 7;
            if (mod == 3)
                return (false, (uint)rm, 0, reg);

            if (!a32)
            {
                if (mod == 0 && rm == 6) // [d16]
                    return (true, F16(), segBase, reg);
                var regsum = rm switch
                {
                    0 => (uint)(cpu.bx + cpu.si),
                    1 => (uint)(cpu.bx + cpu.di),
                    2 => (uint)(cpu.bp + cpu.si),
                    3 => (uint)(cpu.bp + cpu.di),
                    4 => cpu.si,
                    5 => cpu.di,
                    6 => cpu.bp,
                    _ => cpu.bx,
                };
                var sb = rm is 2 or 3 or 6 ? ssSegBase : segBase;
                return mod switch
                {
                    0 => (true, regsum, sb, reg),
                    1 => (true, (uint)(regsum + (sbyte)F8()), sb, reg),
                    _ => (true, regsum + F16(), sb, reg),
                };
            }

            if (rm == 4) // SIB
            {
                var sib = F8();
                var idx = (sib >> 3) & 7;
                var scaled = (uint)((1 << ((sib >> 6) & 3)) * (idx == 4 ? 0 : FReg32(cpu, idx)));
                var basef = sib & 7;
                var sb = basef is 4 or 5 ? ssSegBase : segBase;
                if (mod == 0 && basef == 5) // ベースなし + disp32
                    return (true, scaled + F32(), segBase, reg);
                var bval = FReg32(cpu, basef);
                return mod switch
                {
                    0 => (true, scaled + bval, sb, reg),
                    1 => (true, (uint)(scaled + bval + (sbyte)F8()), sb, reg),
                    _ => (true, scaled + bval + F32(), sb, reg),
                };
            }

            if (mod == 0 && rm == 5) // [d32]
                return (true, F32(), segBase, reg);
            var sb2 = rm == 5 ? ssSegBase : segBase;
            var rv = FReg32(cpu, rm);
            return mod switch
            {
                0 => (true, rv, sb2, reg),
                1 => (true, (uint)(rv + (sbyte)F8()), sb2, reg),
                _ => (true, rv + F32(), sb2, reg),
            };
        }

        uint RegGet(int type, int r) => type == 0 ? FReg8(cpu, r) : type == 1 ? FReg16(cpu, r) : FReg32(cpu, r);
        void RegSet(int type, int r, uint v)
        {
            if (type == 0) FReg8Set(cpu, r, (byte)v);
            else if (type == 1) FReg16Set(cpu, r, (ushort)v);
            else FReg32Set(cpu, r, v);
        }
        uint RmGet(int type, bool isMem, uint a) =>
            isMem
                ? (type == 0 ? EnvGetMemoryData8(env, a) : type == 1 ? EnvGetMemoryData16(env, a) : EnvGetMemoryData32(env, a))
                : RegGet(type, (int)a);
        void RmSet(int type, bool isMem, uint a, uint v)
        {
            if (isMem)
            {
                if (type == 0) FWrite8(env, a, (byte)v);
                else if (type == 1) FWrite16(env, a, (ushort)v);
                else FWrite32(env, a, v);
            }
            else
                RegSet(type, (int)a, v);
        }

        var op = F8();

        switch (op)
        {
            case <= 0x3D when (op & 7) <= 5: // ALU 00-3D (ADD/OR/ADC/SBB/AND/SUB/XOR/CMP)
            {
                var kind = (op >> 3) & 7;
                var form = op & 7;
                if (form < 4) // r/m, reg 形式
                {
                    var type = (form & 1) != 0 ? typeW : 0;
                    var d = (form & 2) != 0;
                    var (isMem, off, segB, reg) = ReadModRM();
                    var addr = isMem ? off + segB : off;
                    var rmVal = RmGet(type, isMem, addr);
                    var regVal = RegGet(type, reg);
                    var r = FCalc(cpu, type, d ? regVal : rmVal, d ? rmVal : regVal, kind);
                    // CMP は結果を書き戻さない(モナド版は元の値を書き戻すが、状態としては同一)。
                    if (kind != 7)
                    {
                        if (d) RegSet(type, reg, r);
                        else RmSet(type, isMem, addr, r);
                    }
                }
                else // AL/eAX, imm 形式
                {
                    var type = (form & 1) != 0 ? typeW : 0;
                    var imm = FImm(type);
                    var r = FCalc(cpu, type, RegGet(type, 0), imm, kind);
                    if (kind != 7) RegSet(type, 0, r);
                }
                break;
            }
            case >= 0x40 and <= 0x4F: // INC/DEC r
            {
                var reg = op & 7;
                var delta = op < 0x48 ? 1 : -1;
                var v = RegGet(typeW, reg);
                FIncDec(cpu, typeW, v, delta);
                RegSet(typeW, reg, (uint)(v + delta));
                break;
            }
            case >= 0x50 and <= 0x57: // PUSH r
                FPush(env, cpu, typeW, RegGet(typeW, op & 7));
                break;
            case >= 0x58 and <= 0x5F: // POP r
                RegSet(typeW, op & 7, FPop(env, cpu, typeW));
                break;
            case 0x68: // PUSH imm
                FPush(env, cpu, typeW, FImm(typeW));
                break;
            case 0x69 or 0x6B: // IMUL r, r/m, imm
            {
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                var srcv = RmGet(typeW, isMem, addr);
                long imm = (op & 2) != 0 ? (sbyte)F8() : (typeW == 1 ? (short)F16() : (int)F32());
                long aa = typeW == 1 ? (short)(ushort)srcv : (int)srcv;
                var prod = aa * imm;
                var of = typeW == 1
                    ? prod < short.MinValue || prod > short.MaxValue
                    : prod < int.MinValue || prod > int.MaxValue;
                RegSet(typeW, reg, (uint)prod);
                cpu.cf = of; cpu.of = of;
                break;
            }
            case 0x6A: // PUSH imm8 (符号拡張)
            {
                var imm = (sbyte)F8();
                FPush(env, cpu, typeW, typeW == 2 ? (uint)imm : (ushort)imm);
                break;
            }
            case >= 0x70 and <= 0x7F: // Jcc rel8
            {
                var f = FCond(cpu, op & 0xF);
                int off = (sbyte)F8();
                cpu.eip = (uint)(startEip + len + (f ? off : 0));
                return true;
            }
            case 0x80 or 0x81: // Group1 r/m, imm
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                var a = RmGet(type, isMem, addr);
                var imm = FImm(type);
                var r = FCalc(cpu, type, a, imm, reg);
                if (reg != 7) RmSet(type, isMem, addr, r);
                break;
            }
            case 0x83: // Group1 r/m, imm8(符号拡張)
            {
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                var imm = (uint)(sbyte)F8();
                var a = RmGet(typeW, isMem, addr);
                var r = FCalc(cpu, typeW, a, imm, reg);
                if (reg != 7) RmSet(typeW, isMem, addr, r);
                break;
            }
            case 0x84 or 0x85: // TEST r/m, r
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                FLogic(cpu, type, RmGet(type, isMem, addr) & RegGet(type, reg));
                break;
            }
            case 0x86 or 0x87: // XCHG r/m, r
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                var rmVal = RmGet(type, isMem, addr);
                var regVal = RegGet(type, reg);
                RmSet(type, isMem, addr, regVal);
                RegSet(type, reg, rmVal);
                break;
            }
            case >= 0x88 and <= 0x8B: // MOV r/m, r ; MOV r, r/m
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                if ((op & 2) != 0) RegSet(type, reg, RmGet(type, isMem, addr));
                else RmSet(type, isMem, addr, RegGet(type, reg));
                break;
            }
            case 0x8D: // LEA(セグメント基底を加算しない実効オフセット)
            {
                var (_, off, _, reg) = ReadModRM();
                RegSet(typeW, reg, off);
                break;
            }
            case 0x90: // NOP
                break;
            case >= 0x91 and <= 0x97: // XCHG eAX, r
            {
                var reg = op & 7;
                var ax = RegGet(typeW, 0);
                var rv = RegGet(typeW, reg);
                RegSet(typeW, 0, rv);
                RegSet(typeW, reg, ax);
                break;
            }
            case 0x98: // CBW(モナド版と同じく操作サイズによらず AL→AX)
                cpu.ax = (ushort)(short)(sbyte)cpu.al;
                break;
            case 0x99: // CWD
                cpu.dx = (ushort)((short)cpu.ax < 0 ? 0xFFFF : 0x0000);
                break;
            case 0x9C: // PUSHF(オペランドサイズに従う)
                FPush(env, cpu, typeW, typeW == 2 ? cpu.eflags : (ushort)cpu.eflags);
                break;
            case 0x9D: // POPF
                if (typeW == 2) cpu.eflags = FPop(env, cpu, 2);
                else cpu.eflags = (cpu.eflags & 0xFFFF0000) | FPop(env, cpu, 1);
                break;
            case 0x9E: // SAHF
                cpu.eflags = (cpu.eflags & 0xFFFFFF00) | cpu.ah | 0x02u;
                break;
            case 0x9F: // LAHF
                cpu.ah = (byte)(cpu.eflags & 0xFF);
                break;
            case >= 0xA0 and <= 0xA3: // MOV acc, [moffs] / MOV [moffs], acc
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var offm = a32 ? F32() : F16();
                var addr = segBase + offm;
                if (op <= 0xA1)
                {
                    if (type == 0) cpu.al = EnvGetMemoryData8(env, addr);
                    else if (type == 1) cpu.ax = EnvGetMemoryData16(env, addr);
                    else cpu.eax = EnvGetMemoryData32(env, addr);
                }
                else
                {
                    if (type == 0) FWrite8(env, addr, cpu.al);
                    else if (type == 1) FWrite16(env, addr, cpu.ax);
                    else FWrite32(env, addr, cpu.eax);
                }
                break;
            }
            case 0xA4 or 0xA5 or 0xA6 or 0xA7 or 0xAA or 0xAB or 0xAC or 0xAD or 0xAE or 0xAF: // 文字列命令(単発)
                FStrOne(env, cpu, op, (op & 1) != 0 ? (typeW == 2 ? 4 : 2) : 1, a32);
                break;
            case 0xA8 or 0xA9: // TEST acc, imm
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var acc = RegGet(type, 0);
                var imm = FImm(type);
                FLogic(cpu, type, acc & imm);
                break;
            }
            case >= 0xB0 and <= 0xBF: // MOV r, imm
            {
                var type = (op & 8) != 0 ? typeW : 0;
                RegSet(type, op & 7, FImm(type));
                break;
            }
            case 0xC0 or 0xC1 or 0xD0 or 0xD1 or 0xD2 or 0xD3: // Group2 シフト/ローテート
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                var v = RmGet(type, isMem, addr);
                int count = op is 0xC0 or 0xC1 ? F8() : op is 0xD2 or 0xD3 ? cpu.cl : 1;
                var cnt = count & 0x1F;
                var res = ComputeShift(v, cnt, reg, Bits(type), cpu.cf);
                if (cnt != 0)
                {
                    cpu.cf = res.cf;
                    cpu.of = res.of;
                    if (reg >= 4) { cpu.zf = res.result == 0; cpu.sf = (res.result & Msb(type)) != 0; }
                }
                RmSet(type, isMem, addr, res.result);
                break;
            }
            case 0xC2: // RET imm16
            {
                var imm = F16();
                var ret = FPop(env, cpu, typeW);
                cpu.eip = typeW == 2 ? ret : ((startEip + len) & 0xFFFF0000) | (ushort)ret;
                if (cpu.code32) cpu.esp += imm; else cpu.sp += imm;
                return true;
            }
            case 0xC3: // RET
            {
                var ret = FPop(env, cpu, typeW);
                cpu.eip = typeW == 2 ? ret : ((startEip + len) & 0xFFFF0000) | (ushort)ret;
                return true;
            }
            case 0xC6 or 0xC7: // MOV r/m, imm
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var (isMem, off, segB, _) = ReadModRM();
                var addr = isMem ? off + segB : off;
                RmSet(type, isMem, addr, FImm(type));
                break;
            }
            case 0xC9: // LEAVE: (E)SP <- (E)BP; (E)BP <- pop()
            {
                if (cpu.code32) cpu.esp = cpu.ebp; else cpu.sp = cpu.bp;
                if (typeW == 2) cpu.ebp = FPop(env, cpu, 2);
                else cpu.bp = (ushort)FPop(env, cpu, 1);
                break;
            }
            case >= 0xE0 and <= 0xE2: // LOOPNE/LOOPE/LOOP(CX は常に 16 ビット)
            {
                int off = (sbyte)F8();
                var newCx = (ushort)(cpu.cx - 1);
                cpu.cx = newCx;
                var cond = (op & 3) switch
                {
                    0 => newCx != 0 && !cpu.zf,
                    1 => newCx != 0 && cpu.zf,
                    _ => newCx != 0,
                };
                cpu.eip = (uint)(startEip + len + (cond ? off : 0));
                return true;
            }
            case 0xE3: // JCXZ
            {
                int off = (sbyte)F8();
                cpu.eip = (uint)(startEip + len + (cpu.cx == 0 ? off : 0));
                return true;
            }
            case 0xE8: // CALL rel
            {
                int off = typeW == 2 ? (int)F32() : (short)F16();
                if (typeW == 2) FPush(env, cpu, 2, startEip + len);
                else FPush(env, cpu, 1, (ushort)(startEip + len));
                cpu.eip = (uint)(startEip + len + off);
                return true;
            }
            case 0xE9: // JMP rel
            {
                int off = typeW == 2 ? (int)F32() : (short)F16();
                cpu.eip = (uint)(startEip + len + off);
                return true;
            }
            case 0xEB: // JMP rel8
            {
                int off = (sbyte)F8();
                cpu.eip = (uint)(startEip + len + off);
                return true;
            }
            case 0xF2 or 0xF3: // REPNE / REP
            {
                var op2 = F8();
                if (op2 == 0x90) break; // F3 90 = PAUSE(NOP 扱い)
                if (op2 is not (0xA4 or 0xA5 or 0xA6 or 0xA7 or 0xAA or 0xAB or 0xAC or 0xAD or 0xAE or 0xAF))
                    return false; // INS/OUTS や非文字列命令はモナド版に委ねる
                var size = (op2 & 1) != 0 ? (typeW == 2 ? 4 : 2) : 1;
                var repZf = op == 0xF3;
                var checkZf = op2 is 0xA6 or 0xA7 or 0xAE or 0xAF;
                while ((a32 ? cpu.ecx : cpu.cx) != 0)
                {
                    FStrOne(env, cpu, op2, size, a32);
                    if (a32) cpu.ecx -= 1; else cpu.cx = (ushort)(cpu.cx - 1);
                    if (checkZf && cpu.zf != repZf) break;
                }
                break;
            }
            case 0xF4: // HLT(モナド版も NOP)
                break;
            case 0xF5: cpu.cf = !cpu.cf; break; // CMC
            case 0xF6 or 0xF7: // Group3
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                var v = RmGet(type, isMem, addr);
                switch (reg)
                {
                    case 0 or 1: // TEST r/m, imm
                        FLogic(cpu, type, v & FImm(type));
                        break;
                    case 2: // NOT
                        RmSet(type, isMem, addr, ~v);
                        break;
                    case 3: // NEG
                        FSub(cpu, type, 0, v);
                        RmSet(type, isMem, addr, 0u - v);
                        break;
                    case 4: // MUL
                    {
                        uint upper;
                        if (type == 0) { var p = (uint)(cpu.al * v); upper = (p >> 8) & 0xFF; cpu.ax = (ushort)p; }
                        else if (type == 1) { var p = cpu.ax * v; upper = (p >> 16) & 0xFFFF; cpu.ax = (ushort)p; cpu.dx = (ushort)(p >> 16); }
                        else { var p = (ulong)cpu.eax * v; upper = (uint)(p >> 32); cpu.eax = (uint)p; cpu.edx = (uint)(p >> 32); }
                        cpu.cf = upper != 0; cpu.of = upper != 0;
                        break;
                    }
                    case 5: // IMUL
                    {
                        var prod = type == 0 ? (sbyte)cpu.al * (sbyte)(byte)v
                                 : type == 1 ? (short)cpu.ax * (short)(ushort)v
                                 : (long)(int)cpu.eax * (int)v;
                        var of = type == 0 ? prod < sbyte.MinValue || prod > sbyte.MaxValue
                               : type == 1 ? prod < short.MinValue || prod > short.MaxValue
                               : prod < int.MinValue || prod > int.MaxValue;
                        if (type == 0) cpu.ax = (ushort)(short)prod;
                        else if (type == 1) { cpu.ax = (ushort)prod; cpu.dx = (ushort)(prod >> 16); }
                        else { cpu.eax = (uint)prod; cpu.edx = (uint)(prod >> 32); }
                        cpu.cf = of; cpu.of = of;
                        break;
                    }
                    case 6: // DIV(0 除算はモナド版と同じく例外で停止。eip は命令の後ろを指す)
                        cpu.eip = startEip + len;
                        if (v == 0) throw new DivideByZeroException();
                        if (type == 0) { uint dividend = cpu.ax; cpu.al = (byte)(dividend / v); cpu.ah = (byte)(dividend % v); }
                        else if (type == 1) { var dividend = ((uint)cpu.dx << 16) | cpu.ax; cpu.ax = (ushort)(dividend / v); cpu.dx = (ushort)(dividend % v); }
                        else { var dividend = ((ulong)cpu.edx << 32) | cpu.eax; cpu.eax = (uint)(dividend / v); cpu.edx = (uint)(dividend % v); }
                        return true;
                    case 7: // IDIV
                        cpu.eip = startEip + len;
                        if (v == 0) throw new DivideByZeroException();
                        if (type == 0) { int dividend = (short)cpu.ax; cpu.al = (byte)(sbyte)(dividend / (sbyte)(byte)v); cpu.ah = (byte)(sbyte)(dividend % (sbyte)(byte)v); }
                        else if (type == 1) { var dividend = (int)(((uint)cpu.dx << 16) | cpu.ax); cpu.ax = (ushort)(short)(dividend / (short)(ushort)v); cpu.dx = (ushort)(short)(dividend % (short)(ushort)v); }
                        else { var dividend = (long)(((ulong)cpu.edx << 32) | cpu.eax); cpu.eax = (uint)(dividend / (int)v); cpu.edx = (uint)(dividend % (int)v); }
                        return true;
                }
                break;
            }
            case 0xF8: cpu.cf = false; break; // CLC
            case 0xF9: cpu.cf = true; break;  // STC
            case 0xFA: cpu.jf = false; break; // CLI
            case 0xFB: cpu.jf = true; break;  // STI
            case 0xFC: cpu.df = false; break; // CLD
            case 0xFD: cpu.df = true; break;  // STD
            case 0xFE: // Group4: INC/DEC r/m8
            {
                var (isMem, off, segB, reg) = ReadModRM();
                if (reg > 1) return false;
                var addr = isMem ? off + segB : off;
                var v = RmGet(0, isMem, addr);
                var delta = reg == 0 ? 1 : -1;
                FIncDec(cpu, 0, v, delta);
                RmSet(0, isMem, addr, (uint)(v + delta));
                break;
            }
            case 0xFF: // Group5: INC/DEC/CALL/JMP/PUSH r/m
            {
                var (isMem, off, segB, reg) = ReadModRM();
                if (reg is 3 or 5 or 7) return false; // far 系はモナド版へ(未実装として停止する)
                var addr = isMem ? off + segB : off;
                var v = RmGet(typeW, isMem, addr);
                switch (reg)
                {
                    case 0 or 1:
                    {
                        var delta = reg == 0 ? 1 : -1;
                        FIncDec(cpu, typeW, v, delta);
                        RmSet(typeW, isMem, addr, (uint)(v + delta));
                        break;
                    }
                    case 2: // CALL 近傍間接
                        if (typeW == 2) FPush(env, cpu, 2, startEip + len);
                        else FPush(env, cpu, 1, (ushort)(startEip + len));
                        cpu.eip = typeW == 2 ? v : ((startEip + len) & 0xFFFF0000) | (v & 0xFFFF);
                        return true;
                    case 4: // JMP 近傍間接
                        cpu.eip = typeW == 2 ? v : ((startEip + len) & 0xFFFF0000) | (v & 0xFFFF);
                        return true;
                    case 6: // PUSH r/m
                        FPush(env, cpu, typeW, v);
                        break;
                }
                break;
            }
            case 0x0F: // 2 バイトオペコード
            {
                var op2 = F8();
                switch (op2)
                {
                    case >= 0x80 and <= 0x8F: // Jcc rel16/32
                    {
                        var f = FCond(cpu, op2 & 0xF);
                        int off = typeW == 2 ? (int)F32() : (short)F16();
                        cpu.eip = (uint)(startEip + len + (f ? off : 0));
                        return true;
                    }
                    case >= 0x90 and <= 0x9F: // SETcc r/m8
                    {
                        var (isMem, off, segB, _) = ReadModRM();
                        var addr = isMem ? off + segB : off;
                        RmSet(0, isMem, addr, FCond(cpu, op2 & 0xF) ? 1u : 0u);
                        break;
                    }
                    case 0xAF: // IMUL r, r/m
                    {
                        var (isMem, off, segB, reg) = ReadModRM();
                        var addr = isMem ? off + segB : off;
                        var srcv = RmGet(typeW, isMem, addr);
                        var dstv = RegGet(typeW, reg);
                        long aa = typeW == 1 ? (short)(ushort)dstv : (int)dstv;
                        long bb = typeW == 1 ? (short)(ushort)srcv : (int)srcv;
                        var prod = aa * bb;
                        var of = typeW == 1
                            ? prod < short.MinValue || prod > short.MaxValue
                            : prod < int.MinValue || prod > int.MaxValue;
                        RegSet(typeW, reg, (uint)prod);
                        cpu.cf = of; cpu.of = of;
                        break;
                    }
                    case 0xB6 or 0xB7 or 0xBE or 0xBF: // MOVZX/MOVSX
                    {
                        var srcW = (op2 & 1) != 0;
                        var signed = (op2 & 8) != 0;
                        var (isMem, off, segB, reg) = ReadModRM();
                        var addr = isMem ? off + segB : off;
                        var value = srcW
                            ? (signed ? (uint)(short)(ushort)RmGet(1, isMem, addr) : RmGet(1, isMem, addr))
                            : (signed ? (uint)(sbyte)(byte)RmGet(0, isMem, addr) : RmGet(0, isMem, addr));
                        RegSet(typeW, reg, value);
                        break;
                    }
                    default:
                        return false;
                }
                break;
            }
            default:
                return false;
        }

        cpu.eip = startEip + len;
        return true;
    }

    // 文字列命令を 1 要素分だけ実行する(REP ループ・単発の両方から使う)。
    // モナド版と同じく、セグメントオーバーライドは無視して DS/ES の基底を直接使う。
    static void FStrOne(EmuEnvironment env, CPU c, byte op, int size, bool a32)
    {
        switch (op)
        {
            case 0xA4 or 0xA5: // MOVS
            {
                var src = c.ds_base + (a32 ? c.esi : c.si);
                var dst = c.es_base + (a32 ? c.edi : c.di);
                FWriteN(env, dst, size, FReadN(env, src, size));
                FStrAdvance(c, size, a32, si: true, di: true);
                break;
            }
            case 0xA6 or 0xA7: // CMPS
            {
                var s = FReadN(env, c.ds_base + (a32 ? c.esi : c.si), size);
                var d = FReadN(env, c.es_base + (a32 ? c.edi : c.di), size);
                FSub(c, size == 1 ? 0 : size == 2 ? 1 : 2, s, d);
                FStrAdvance(c, size, a32, si: true, di: true);
                break;
            }
            case 0xAA or 0xAB: // STOS
            {
                var dst = c.es_base + (a32 ? c.edi : c.di);
                FWriteN(env, dst, size, size == 1 ? c.al : size == 2 ? c.ax : c.eax);
                FStrAdvance(c, size, a32, si: false, di: true);
                break;
            }
            case 0xAC or 0xAD: // LODS
            {
                var v = FReadN(env, c.ds_base + (a32 ? c.esi : c.si), size);
                if (size == 1) c.al = (byte)v; else if (size == 2) c.ax = (ushort)v; else c.eax = v;
                FStrAdvance(c, size, a32, si: true, di: false);
                break;
            }
            case 0xAE or 0xAF: // SCAS
            {
                uint a = size == 1 ? c.al : size == 2 ? c.ax : c.eax;
                var m = FReadN(env, c.es_base + (a32 ? c.edi : c.di), size);
                FSub(c, size == 1 ? 0 : size == 2 ? 1 : 2, a, m);
                FStrAdvance(c, size, a32, si: false, di: true);
                break;
            }
        }
    }

    // DF に従って SI/DI(a32 なら ESI/EDI)を size 分だけ増減する。
    static void FStrAdvance(CPU c, int size, bool a32, bool si, bool di)
    {
        var delta = c.df ? -size : size;
        if (si) { if (a32) c.esi = (uint)(c.esi + delta); else c.si = (ushort)(c.si + delta); }
        if (di) { if (a32) c.edi = (uint)(c.edi + delta); else c.di = (ushort)(c.di + delta); }
    }

    static uint FReadN(EmuEnvironment env, uint addr, int size) =>
        size == 1 ? EnvGetMemoryData8(env, addr) : size == 2 ? EnvGetMemoryData16(env, addr) : EnvGetMemoryData32(env, addr);

    static void FWriteN(EmuEnvironment env, uint addr, int size, uint v)
    {
        if (size == 1) FWrite8(env, addr, (byte)v);
        else if (size == 2) FWrite16(env, addr, (ushort)v);
        else FWrite32(env, addr, v);
    }

    // メモリ書き込み。RAM 外は 1 バイト単位で無視する(EnvSetMemoryDatas と同じ挙動)。
    static void FWrite8(EmuEnvironment env, uint a, byte v)
    {
        var m = env.OneMegaMemory_;
        if (a < (uint)m.Length) m[a] = v;
    }

    static void FWrite16(EmuEnvironment env, uint a, ushort v)
    {
        var m = env.OneMegaMemory_;
        if (a <= (uint)m.Length - 2) { m[a] = (byte)v; m[a + 1] = (byte)(v >> 8); }
        else { FWrite8(env, a, (byte)v); FWrite8(env, a + 1, (byte)(v >> 8)); }
    }

    static void FWrite32(EmuEnvironment env, uint a, uint v)
    {
        var m = env.OneMegaMemory_;
        if (a <= (uint)m.Length - 4)
        {
            m[a] = (byte)v; m[a + 1] = (byte)(v >> 8); m[a + 2] = (byte)(v >> 16); m[a + 3] = (byte)(v >> 24);
        }
        else
        {
            FWrite8(env, a, (byte)v); FWrite8(env, a + 1, (byte)(v >> 8));
            FWrite8(env, a + 2, (byte)(v >> 16)); FWrite8(env, a + 3, (byte)(v >> 24));
        }
    }

    // PUSH: SP/ESP を減らしてからスタックトップへ書く(Push と同一)。
    static void FPush(EmuEnvironment env, CPU c, int type, uint v)
    {
        var size = type == 0 ? 1 : type == 1 ? 2 : 4;
        if (c.code32) c.esp = (uint)(c.esp - size); else c.sp = (ushort)(c.sp - size);
        var addr = c.ss_base + (c.code32 ? c.esp : c.sp);
        if (type == 0) FWrite8(env, addr, (byte)v);
        else if (type == 1) FWrite16(env, addr, (ushort)v);
        else FWrite32(env, addr, v);
    }

    // POP: スタックトップから読んでから SP/ESP を増やす(Pop と同一)。
    static uint FPop(EmuEnvironment env, CPU c, int type)
    {
        var addr = c.ss_base + (c.code32 ? c.esp : c.sp);
        var v = type == 0 ? EnvGetMemoryData8(env, addr)
              : type == 1 ? EnvGetMemoryData16(env, addr)
              : EnvGetMemoryData32(env, addr);
        var size = type == 0 ? 1 : type == 1 ? 2 : 4;
        if (c.code32) c.esp = (uint)(c.esp + size); else c.sp = (ushort)(c.sp + size);
        return v;
    }

    // 算術/論理グループ(Calc と同一の計算とフラグ更新。PF/AF は更新しない)。
    static uint FCalc(CPU c, int type, uint a, uint b0, int kind)
    {
        var mask = Mask(type);
        var msb = Msb(type);
        a &= mask;
        var b = (b0 + (kind is 2 or 3 && c.cf ? 1u : 0u)) & mask;
        var r = kind switch
        {
            0 or 2 => (a + b) & mask,   // ADD/ADC
            1 => a | b,                 // OR
            3 or 5 => (a - b) & mask,   // SBB/SUB
            4 => a & b,                 // AND
            6 => a ^ b,                 // XOR
            _ => a,                     // CMP
        };
        var isAdd = kind is 0 or 2;
        var isSub = kind is 3 or 5 or 7;
        var fr = isSub ? (a - b) & mask : r;
        c.cf = isAdd ? (ulong)a + b > mask : isSub && a < b;
        c.zf = fr == 0;
        c.sf = (fr & msb) != 0;
        c.of = isAdd ? ((a ^ b) & msb) == 0 && ((a ^ fr) & msb) != 0
                     : isSub && ((a ^ b) & msb) != 0 && ((a ^ fr) & msb) != 0;
        return r;
    }

    // 減算フラグ(update_eflags_sub と同一)。NEG/CMPS/SCAS 用。
    static void FSub(CPU c, int type, uint v1, uint v2)
    {
        var mask = Mask(type);
        var msb = Msb(type);
        v1 &= mask; v2 &= mask;
        var d = (v1 - v2) & mask;
        c.cf = v1 < v2;
        c.zf = v1 == v2;
        c.sf = (d & msb) != 0;
        c.of = ((v1 ^ v2) & msb) != 0 && ((v1 ^ d) & msb) != 0;
    }

    // 論理演算フラグ(update_eflags と同一: CF=OF=0, ZF/SF のみ)。
    static void FLogic(CPU c, int type, uint r)
    {
        r &= Mask(type);
        c.cf = false;
        c.zf = r == 0;
        c.sf = (r & Msb(type)) != 0;
        c.of = false;
    }

    // INC/DEC フラグ(update_eflags_incdec と同一: CF は変更しない)。
    static void FIncDec(CPU c, int type, uint v, int delta)
    {
        var msb = Msb(type);
        var r = (uint)(v + delta) & Mask(type);
        c.zf = r == 0;
        c.sf = (r & msb) != 0;
        c.of = (v & Mask(type)) == (delta > 0 ? msb - 1 : msb);
    }

    // Jcc 条件(モナド版 Jcc と同一)。
    static bool FCond(CPU c, int t) => t switch
    {
        0 => c.of,
        1 => !c.of,
        2 => c.cf,
        3 => !c.cf,
        4 => c.zf,
        5 => !c.zf,
        6 => c.cf || c.zf,
        7 => !c.cf && !c.zf,
        8 => c.sf,
        9 => !c.sf,
        10 => c.pf,
        11 => !c.pf,
        12 => c.sf != c.of,
        13 => c.sf == c.of,
        14 => c.zf || c.sf != c.of,
        _ => c.sf == c.of && !c.zf,
    };

    static byte FReg8(CPU c, int r) => r switch
    {
        0 => c.al, 1 => c.cl, 2 => c.dl, 3 => c.bl,
        4 => c.ah, 5 => c.ch, 6 => c.dh, _ => c.bh,
    };

    static void FReg8Set(CPU c, int r, byte v)
    {
        switch (r)
        {
            case 0: c.al = v; break;
            case 1: c.cl = v; break;
            case 2: c.dl = v; break;
            case 3: c.bl = v; break;
            case 4: c.ah = v; break;
            case 5: c.ch = v; break;
            case 6: c.dh = v; break;
            default: c.bh = v; break;
        }
    }

    static ushort FReg16(CPU c, int r) => r switch
    {
        0 => c.ax, 1 => c.cx, 2 => c.dx, 3 => c.bx,
        4 => c.sp, 5 => c.bp, 6 => c.si, _ => c.di,
    };

    static void FReg16Set(CPU c, int r, ushort v)
    {
        switch (r)
        {
            case 0: c.ax = v; break;
            case 1: c.cx = v; break;
            case 2: c.dx = v; break;
            case 3: c.bx = v; break;
            case 4: c.sp = v; break;
            case 5: c.bp = v; break;
            case 6: c.si = v; break;
            default: c.di = v; break;
        }
    }

    static uint FReg32(CPU c, int r) => r switch
    {
        0 => c.eax, 1 => c.ecx, 2 => c.edx, 3 => c.ebx,
        4 => c.esp, 5 => c.ebp, 6 => c.esi, _ => c.edi,
    };

    static void FReg32Set(CPU c, int r, uint v)
    {
        switch (r)
        {
            case 0: c.eax = v; break;
            case 1: c.ecx = v; break;
            case 2: c.edx = v; break;
            case 3: c.ebx = v; break;
            case 4: c.esp = v; break;
            case 5: c.ebp = v; break;
            case 6: c.esi = v; break;
            default: c.edi = v; break;
        }
    }
}
