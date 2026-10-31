using static Emu86.Alu;
using static Emu86.Conversions;
using static Emu86.Operands;
using static Emu86.Ports;
using static Emu86.Mmu;
using static Emu86.Decoder;

namespace Emu86;

// 方針B: 命令的な高速実行コア。
// モナド版(Program.Execute2)と 1 命令単位で完全に同じ状態遷移を行う「速い写像」であり、
// 意味論の正は常にモナド版に置く(分岐トレースのバイト一致で回帰検証する)。
// 未対応の命令は CPU を一切変更せずに false を返し、呼び出し側がモナド版へフォールバックする。
static public class FastCore
{
    // 1 命令を実行できたら true(cpu は更新済み)。対応外なら cpu 無変更で false。
    // デコード中はローカル変数だけを使い、コミット(cpu への書き込み)は実行が確定してから行う。
    static public bool FastStep(EmuEnvironment env, CPU cpu)
    {
        var startEip = cpu.eip;
        var code32 = cpu.code32;
        var csBase = cpu.cs_base;
        uint len = 0;

        // コードフェッチ。16 ビットコードでは IP(下位 16 ビット)でラップする(CodeSource がモナド版と共有)。
        var code = new CodeSource(env, csBase, startEip, code32);
        byte F8() { var v = code.Byte((int)len); len += 1; return v; }
        ushort F16() { var v = code.Word((int)len); len += 2; return v; }
        uint F32() { var v = code.Dword((int)len); len += 4; return v; }
        uint FImm(int type) => type == 0 ? F8() : type == 1 ? F16() : F32();

        // プレフィックス(モナド版と同じパーサ。複数のセグメントオーバーライドは ES>CS>SS>DS>FS>GS の優先順)。
        len = (uint)ScanPrefixes(code, 0, out var prefixes);
        var p66 = (prefixes & Prefix.OperandSize) != 0;
        var p67 = (prefixes & Prefix.AddressSize) != 0;

        var typeW = (code32 != p66) ? 2 : 1;  // w=1 の実効オペランドサイズ(1=word, 2=dword)
        var a32 = code32 != p67;              // 実効アドレスサイズ

        var ovr = OverrideBase(prefixes, cpu);
        var segBase = ovr ?? cpu.ds_base;
        var ssSegBase = ovr ?? cpu.ss_base;

        // ModRM(+SIB+disp)を読み、(メモリか, 実効オフセット, セグメント基底, reg フィールド) を返す。
        // mod=3 のときオフセットはレジスタ番号。構造の読み取りと実効アドレスの計算はモナド版と共有する。
        (bool isMem, uint off, uint segB, int reg) ReadModRM()
        {
            var modrmByte = F8();
            var modrm = ParseModRm(code, (int)len, modrmByte, a32);
            len += (uint)(modrm.Length - 1);
            var (isMem, off, segB) = EffectiveAddress(cpu, modrm, a32, segBase, ssSegBase);
            return (isMem, off, segB, modrm.Reg);
        }

        uint RegGet(int type, int r) => type == 0 ? FReg8(cpu, r) : type == 1 ? FReg16(cpu, r) : ReadRegister32(cpu, r);
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
                if (type == 0) EnvWriteByte(env, a, (byte)v);
                else if (type == 1) EnvWriteWord(env, a, (ushort)v);
                else EnvWriteDword(env, a, v);
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
                    var r = CalculateAlu(cpu, type, d ? regVal : rmVal, d ? rmVal : regVal, kind);
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
                    var r = CalculateAlu(cpu, type, RegGet(type, 0), imm, kind);
                    if (kind != 7) RegSet(type, 0, r);
                }
                break;
            }
            case >= 0x40 and <= 0x4F: // INC/DEC r
            {
                var reg = op & 7;
                var delta = op < 0x48 ? 1 : -1;
                var v = RegGet(typeW, reg);
                SetIncDecFlags(cpu, typeW, v, delta);
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
                var f = EvaluateCondition(cpu, op & 0xF);
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
                var r = CalculateAlu(cpu, type, a, imm, reg);
                if (reg != 7) RmSet(type, isMem, addr, r);
                break;
            }
            case 0x83: // Group1 r/m, imm8(符号拡張)
            {
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                var imm = (uint)(sbyte)F8();
                var a = RmGet(typeW, isMem, addr);
                var r = CalculateAlu(cpu, typeW, a, imm, reg);
                if (reg != 7) RmSet(typeW, isMem, addr, r);
                break;
            }
            case 0x84 or 0x85: // TEST r/m, r
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var (isMem, off, segB, reg) = ReadModRM();
                var addr = isMem ? off + segB : off;
                SetLogicFlags(cpu, type, RmGet(type, isMem, addr) & RegGet(type, reg));
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
            case 0x98: // CBW/CWDE(オペランドサイズに従う)
                if (typeW == 2) cpu.eax = (uint)(int)(short)cpu.ax;
                else cpu.ax = (ushort)(short)(sbyte)cpu.al;
                break;
            case 0x99: // CWD/CDQ
                if (typeW == 2) cpu.edx = (int)cpu.eax < 0 ? 0xFFFFFFFF : 0;
                else cpu.dx = (ushort)((short)cpu.ax < 0 ? 0xFFFF : 0x0000);
                break;
            case 0x9C: // PUSHF(オペランドサイズに従う)
                FPush(env, cpu, typeW, typeW == 2 ? cpu.eflags : (ushort)cpu.eflags);
                break;
            case 0x9D: // POPF(CPL>0 の IOPL/IF/VM 保護はモナド版に委ねる)
                if (cpu.pe && cpu.cpl != 0) return false;
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
                    if (type == 0) EnvWriteByte(env, addr, cpu.al);
                    else if (type == 1) EnvWriteWord(env, addr, cpu.ax);
                    else EnvWriteDword(env, addr, cpu.eax);
                }
                break;
            }
            case 0x6C or 0x6D or 0x6E or 0x6F or 0xA4 or 0xA5 or 0xA6 or 0xA7 or 0xAA or 0xAB or 0xAC or 0xAD or 0xAE or 0xAF: // 文字列命令(単発)
                FStrOne(env, cpu, op, (op & 1) != 0 ? (typeW == 2 ? 4 : 2) : 1, a32, segBase);
                break;
            case 0xA8 or 0xA9: // TEST acc, imm
            {
                var type = (op & 1) != 0 ? typeW : 0;
                var acc = RegGet(type, 0);
                var imm = FImm(type);
                SetLogicFlags(cpu, type, acc & imm);
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
                    if (reg >= 4) { cpu.zf = res.result == 0; cpu.sf = (res.result & Msb(type)) != 0; cpu.pf = Par(res.result); }
                }
                RmSet(type, isMem, addr, res.result);
                break;
            }
            case 0xC2: // RET imm16
            {
                var imm = F16();
                var ret = FPop(env, cpu, typeW);
                cpu.eip = typeW == 2 ? ret : ((startEip + len) & 0xFFFF0000) | (ushort)ret;
                if (cpu.stack32) cpu.esp += imm; else cpu.sp += imm;
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
                if (cpu.stack32) cpu.esp = cpu.ebp; else cpu.sp = cpu.bp;
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
                if (op2 is not (0x6C or 0x6D or 0x6E or 0x6F or 0xA4 or 0xA5 or 0xA6 or 0xA7 or 0xAA or 0xAB or 0xAC or 0xAD or 0xAE or 0xAF))
                    return false; // 非文字列命令はモナド版に委ねる
                var size = (op2 & 1) != 0 ? (typeW == 2 ? 4 : 2) : 1;
                var repZf = op == 0xF3;
                var checkZf = op2 is 0xA6 or 0xA7 or 0xAE or 0xAF;
                while ((a32 ? cpu.ecx : cpu.cx) != 0)
                {
                    FStrOne(env, cpu, op2, size, a32, segBase);
                    if (a32) cpu.ecx -= 1; else cpu.cx = (ushort)(cpu.cx - 1);
                    cpu.Decode.CommitRepeat(cpu);
                    if (checkZf && cpu.zf != repZf) break;
                }
                break;
            }
            case 0xF4: // HLT: ランナーが待機(次の割り込みまで仮想時間を進める)に使う印を立てる
                cpu.Halted = true;
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
                        SetLogicFlags(cpu, type, v & FImm(type));
                        break;
                    case 2: // NOT
                        RmSet(type, isMem, addr, ~v);
                        break;
                    case 3: // NEG
                        SetSubtractionFlags(cpu, type, 0, v);
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
                SetIncDecFlags(cpu, 0, v, delta);
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
                        SetIncDecFlags(cpu, typeW, v, delta);
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
                        var f = EvaluateCondition(cpu, op2 & 0xF);
                        int off = typeW == 2 ? (int)F32() : (short)F16();
                        cpu.eip = (uint)(startEip + len + (f ? off : 0));
                        return true;
                    }
                    case >= 0x90 and <= 0x9F: // SETcc r/m8
                    {
                        var (isMem, off, segB, _) = ReadModRM();
                        var addr = isMem ? off + segB : off;
                        RmSet(0, isMem, addr, EvaluateCondition(cpu, op2 & 0xF) ? 1u : 0u);
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
    // source は源(DS:SI)の基底(セグメントオーバーライド適用済み)。先(ES:DI)は固定。
    static void FStrOne(EmuEnvironment env, CPU c, byte op, int size, bool a32, uint source)
    {
        switch (op)
        {
            case 0xA4 or 0xA5: // MOVS
            {
                var src = source + (a32 ? c.esi : c.si);
                var dst = c.es_base + (a32 ? c.edi : c.di);
                EnvWriteN(env, dst, size, EnvReadN(env, src, size));
                FStrAdvance(c, size, a32, si: true, di: true);
                break;
            }
            case 0xA6 or 0xA7: // CMPS
            {
                var s = EnvReadN(env, source + (a32 ? c.esi : c.si), size);
                var d = EnvReadN(env, c.es_base + (a32 ? c.edi : c.di), size);
                SetSubtractionFlags(c, size == 1 ? 0 : size == 2 ? 1 : 2, s, d);
                FStrAdvance(c, size, a32, si: true, di: true);
                break;
            }
            case 0xAA or 0xAB: // STOS
            {
                var dst = c.es_base + (a32 ? c.edi : c.di);
                EnvWriteN(env, dst, size, size == 1 ? c.al : size == 2 ? c.ax : c.eax);
                FStrAdvance(c, size, a32, si: false, di: true);
                break;
            }
            case 0xAC or 0xAD: // LODS
            {
                var v = EnvReadN(env, source + (a32 ? c.esi : c.si), size);
                if (size == 1) c.al = (byte)v; else if (size == 2) c.ax = (ushort)v; else c.eax = v;
                FStrAdvance(c, size, a32, si: true, di: false);
                break;
            }
            case 0x6C or 0x6D: // INS: ポート DX から 1 要素読んで [ES:DI] へ(XP の ATA ドライバは rep insw でセクタを読む)
            {
                var dst = c.es_base + (a32 ? c.edi : c.di);
                EnvWriteN(env, dst, size, EnvInPortN(env, c.dx, size));
                FStrAdvance(c, size, a32, si: false, di: true);
                break;
            }
            case 0x6E or 0x6F: // OUTS: [DS:SI] から 1 要素読んでポート DX へ
            {
                EnvOutPortN(env, c.dx, size, EnvReadN(env, source + (a32 ? c.esi : c.si), size));
                FStrAdvance(c, size, a32, si: true, di: false);
                break;
            }
            case 0xAE or 0xAF: // SCAS
            {
                uint a = size == 1 ? c.al : size == 2 ? c.ax : c.eax;
                var m = EnvReadN(env, c.es_base + (a32 ? c.edi : c.di), size);
                SetSubtractionFlags(c, size == 1 ? 0 : size == 2 ? 1 : 2, a, m);
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

    // PUSH: SP/ESP を減らしてからスタックトップへ書く(Push と同一)。
    // スタック幅は SS.B(stack32)で決まる(CS.D ではない)。
    static internal void FPush(EmuEnvironment env, CPU c, int type, uint v)
    {
        var size = Bytes(type);
        if (c.stack32) c.esp = (uint)(c.esp - size); else c.sp = (ushort)(c.sp - size);
        var addr = c.ss_base + (c.stack32 ? c.esp : c.sp);
        if (type == 0) EnvWriteByte(env, addr, (byte)v);
        else if (type == 1) EnvWriteWord(env, addr, (ushort)v);
        else EnvWriteDword(env, addr, v);
    }

    // POP: スタックトップから読んでから SP/ESP を増やす(Pop と同一)。
    static internal uint FPop(EmuEnvironment env, CPU c, int type)
    {
        var addr = c.ss_base + (c.stack32 ? c.esp : c.sp);
        var v = type == 0 ? EnvGetMemoryData8(env, addr)
              : type == 1 ? EnvGetMemoryData16(env, addr)
              : EnvGetMemoryData32(env, addr);
        var size = Bytes(type);
        if (c.stack32) c.esp = (uint)(c.esp + size); else c.sp = (ushort)(c.sp + size);
        return v;
    }

    static internal byte FReg8(CPU c, int r) => r switch
    {
        0 => c.al, 1 => c.cl, 2 => c.dl, 3 => c.bl,
        4 => c.ah, 5 => c.ch, 6 => c.dh, _ => c.bh,
    };

    static internal void FReg8Set(CPU c, int r, byte v)
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

    static internal ushort FReg16(CPU c, int r) => r switch
    {
        0 => c.ax, 1 => c.cx, 2 => c.dx, 3 => c.bx,
        4 => c.sp, 5 => c.bp, 6 => c.si, _ => c.di,
    };

    static internal void FReg16Set(CPU c, int r, ushort v)
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

    static internal void FReg32Set(CPU c, int r, uint v)
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
