using static Emu86.Operands;
using static Emu86.Mmu;
using static Emu86.Ports;
using static Emu86.DataAccess;
using static Emu86.Conversions;
using static Emu86.CpuStates;
using static Emu86.Decoder;

namespace Emu86;

// 文字列命令(MOVS/STOS/LODS/CMPS/SCAS/INS/OUTS)の 1 要素分。
//   要素の幅(type)      : operand size に従う(w かつ 32bit なら dword、そうでなければ word/byte)= OperandType(w)。
//   アドレスモード     : code32 でフラット32bit(ESI/EDI, ベース0)、そうでなければ real(DS:SI/ES:DI)。
//   源(DS:SI)はセグメントオーバーライドで差し替えられる。先(ES:DI)は固定。
// 通常コア向けは、源・先・ポート・アキュムレータをオペランド(Lens)にして、7 命令を 1 つの骨格(PerElement)と Move/Binary で書く。
// IR 向けは Element(命令的な形)。
static public class StringOps
{
    static bool StrA32(CPU cpu) => cpu.code32 != cpu.address_size_prefix;

    // 源 [DS:SI] と先 [ES:DI] のメモリアドレス(アドレスサイズが 32 ビットなら ESI/EDI、そうでなければ SI/DI)。
    static readonly State<(MemAddr source, MemAddr destination)> Addresses = GetDataFromCpu(cpu => (
        source: (MemAddr)(true, (OverrideBase(cpu.Decode.Prefixes, cpu) ?? cpu.ds_base) + (StrA32(cpu) ? cpu.esi : cpu.si)),
        destination: (MemAddr)(true, cpu.es_base + (StrA32(cpu) ? cpu.edi : cpu.di))));

    // 1 要素のオペランド: 源、先、アキュムレータ(AL/AX/EAX)、ポート DX。
    // 命令が使うものだけを組み立てる(REP の反復ごとに通るので、使わない Lens は作らない)。
    readonly record struct StringOperands(MemAddr SourceAddress, MemAddr DestinationAddress, int Type)
    {
        public Operand Source => Rm(SourceAddress, Type);
        public Operand Destination => Rm(DestinationAddress, Type);
        public Operand Accumulator => Reg(0, Type);
        public Operand Port => Operands.Port(Dx, Type);
    }

    // 1 要素分の骨格: オペランドの間で operation(転送か比較)を行い、使ったインデックス(SI/DI)を DF に従って 1 要素分進める。
    static State<Unit> PerElement(bool w, bool si, bool di, Func<StringOperands, State<Unit>> operation) =>
        from type in OperandType(w)
        from address in Addresses
        from _1 in operation(new(address.source, address.destination, type))
        from _2 in Effect(cpu => Advance(cpu, Bytes(type), StrA32(cpu), si, di))
        select Unit.unit;

    // 7 つの文字列命令は「どこからどこへ」と「どのインデックスを進めるか」だけが違う。比較(CMPS/SCAS)は CMP と同じく結果を捨ててフラグだけ。
    static public State<Unit> Movs(bool w) => PerElement(w, si: true, di: true, operands => Move(operands.Destination, operands.Source));            // [ES:DI] <- [DS:SI]

    static public State<Unit> Stos(bool w) => PerElement(w, si: false, di: true, operands => Move(operands.Destination, operands.Accumulator));      // [ES:DI] <- アキュムレータ

    static public State<Unit> Lods(bool w) => PerElement(w, si: true, di: false, operands => Move(operands.Accumulator, operands.Source));           // アキュムレータ <- [DS:SI]

    static public State<Unit> Cmps(bool w) => PerElement(w, si: true, di: true, operands => Binary(operands.Source, operands.Destination, 7));       // [DS:SI] - [ES:DI]

    static public State<Unit> Scas(bool w) => PerElement(w, si: false, di: true, operands => Binary(operands.Accumulator, operands.Destination, 7)); // アキュムレータ - [ES:DI]

    static public State<Unit> Ins(bool w) => PerElement(w, si: false, di: true, operands => Move(operands.Destination, operands.Port));              // [ES:DI] <- ポート DX

    static public State<Unit> Outs(bool w) => PerElement(w, si: true, di: false, operands => Move(operands.Port, operands.Source));                  // ポート DX <- [DS:SI]

    // 1 要素分の命令的な形(IR の StringStmt が使う)。source は源(DS:SI)の基底(セグメントオーバーライド適用済み)、先(ES:DI)は固定。
    // 書き込み先の検査は EnvWriteN に任せる(読み取りが先なので、フォルト時に状態は変わっていない)。
    static internal void Element(EmuEnvironment env, CPU c, int op, int size, bool a32, uint source)
    {
        var type = size == 1 ? 0 : size == 2 ? 1 : 2;
        switch (op)
        {
            case 0xA4 or 0xA5: // MOVS
                EnvWriteN(env, c.es_base + (a32 ? c.edi : c.di), size, EnvReadN(env, source + (a32 ? c.esi : c.si), size));
                Advance(c, size, a32, si: true, di: true);
                break;
            case 0xA6 or 0xA7: // CMPS
            {
                var s = EnvReadN(env, source + (a32 ? c.esi : c.si), size);
                var d = EnvReadN(env, c.es_base + (a32 ? c.edi : c.di), size);
                Alu.SetSubtractionFlags(c, type, s, d);
                Advance(c, size, a32, si: true, di: true);
                break;
            }
            case 0xAA or 0xAB: // STOS
                EnvWriteN(env, c.es_base + (a32 ? c.edi : c.di), size, c.eax & Mask(type));
                Advance(c, size, a32, si: false, di: true);
                break;
            case 0xAC or 0xAD: // LODS
            {
                var v = EnvReadN(env, source + (a32 ? c.esi : c.si), size);
                if (size == 1) c.al = (byte)v; else if (size == 2) c.ax = (ushort)v; else c.eax = v;
                Advance(c, size, a32, si: true, di: false);
                break;
            }
            case 0x6C or 0x6D: // INS: ポート DX から 1 要素読んで [ES:DI] へ(XP の ATA ドライバは rep insw でセクタを読む)
                EnvWriteN(env, c.es_base + (a32 ? c.edi : c.di), size, EnvInPortN(env, c.dx, size));
                Advance(c, size, a32, si: false, di: true);
                break;
            case 0x6E or 0x6F: // OUTS: [DS:SI] から 1 要素読んでポート DX へ
                EnvOutPortN(env, c.dx, size, EnvReadN(env, source + (a32 ? c.esi : c.si), size));
                Advance(c, size, a32, si: true, di: false);
                break;
            case 0xAE or 0xAF: // SCAS
            {
                var m = EnvReadN(env, c.es_base + (a32 ? c.edi : c.di), size);
                Alu.SetSubtractionFlags(c, type, c.eax & Mask(type), m);
                Advance(c, size, a32, si: false, di: true);
                break;
            }
        }
    }

    // DF に従って SI/DI(a32 なら ESI/EDI)を size 分だけ増減する(命令的な形。通常コアの PerElement もこれを呼ぶ)。
    static void Advance(CPU c, int size, bool a32, bool si, bool di)
    {
        var delta = c.df ? -size : size;
        if (si) { if (a32) c.esi = (uint)(c.esi + delta); else c.si = (ushort)(c.si + delta); }
        if (di) { if (a32) c.edi = (uint)(c.edi + delta); else c.di = (ushort)(c.di + delta); }
    }
}
