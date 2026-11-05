using static Emu86.Mmu;
using static Emu86.DataAccess;
using static Emu86.CpuStates;
using static Emu86.Decoder;

namespace Emu86;

// 命令ストリーム(CS:EIP)からの読み取り: オペコード・即値・ModRM(EIP を進める State)。
static public class Fetch
{
    // type(0=byte、1=word、2=dword)の幅の即値を読む。
    static public State<Data> GetMemoryDataIp(int type) => MemoryDataIpByType[type];

    static public State<(int mod, int reg, int rm)> ModRegRm() =>
        from value in GetMemoryDataIp8
        let mod = (value >> 6) & 0x3
        let reg = (value >> 3) & 0x7
        let rm = value & 0x7
        select (mod, reg, rm);

    // ホットパスで毎命令アクセスされるため、プロパティではなくキャッシュ済みフィールドにする
    // (プロパティだと参照のたびに SelectMany のデリゲート鎖を再構築してしまう)。
    static public readonly State<byte> GetMemoryDataIp8 = (env, cpu, opcodes) =>
    {
        var data = EnvGetMemoryData8(env, GetCodeAddr(cpu).addr);
        cpu.eip += 1;
        return (true, data, cpu, string.Empty);
    };

    static public readonly State<ushort> GetMemoryDataIp16 = (env, cpu, opcodes) =>
    {
        var data = EnvGetMemoryData16(env, GetCodeAddr(cpu).addr);
        cpu.eip += 2;
        return (true, data, cpu, string.Empty);
    };

    static public readonly State<uint> GetMemoryDataIp32 = (env, cpu, opcodes) =>
    {
        var data = EnvGetMemoryData32(env, GetCodeAddr(cpu).addr);
        cpu.eip += 4;
        return (true, data, cpu, string.Empty);
    };

    static readonly State<Data>[] MemoryDataIpByType =
    [
        GetMemoryDataIp8.Select(Conversions.ToTypeData),
        GetMemoryDataIp16.Select(Conversions.ToTypeData),
        GetMemoryDataIp32.Select(Conversions.ToTypeData)
    ];

    // オペコード末尾バイトの w ビット(bit0: 0=8 ビット、1=16/32 ビット)。2 バイト命令(0F xx)では 2 バイト目を見る。
    static public readonly State<bool> WidthBit = Opecodes.Select(opecode => 0 != (opecode[^1] & 0x01));

    // w ビットから決めた実効オペランド幅(type)。
    static public readonly State<int> OperandTypeW =
        from w in WidthBit
        from type in OperandType(w)
        select type;

    // 符号拡張した imm8 を type の幅で(Group1 83、PUSH 6A、IMUL 6B)。
    static public State<Data> Imm8SignExtended(int type) =>
        GetMemoryDataIp8.Select(imm => ((uint)(sbyte)imm).ToTypeData(type));

    // 分岐の相対オフセット。rel8 は符号拡張、rel16/rel32 は実効オペランドサイズに従う(rel16 は符号拡張)。
    static public readonly State<int> Rel8 = GetMemoryDataIp8.Select(offset => (int)(sbyte)offset);

    static public readonly State<int> RelNear =
        from type in OperandType(true)
        from offset in type == 2
            ? GetMemoryDataIp32.Select(dd => (int)dd)
            : GetMemoryDataIp16.Select(dw => (int)(short)dw)
        select offset;

    // ModRM の残り(SIB・変位)を現在の (E)IP から読み、実効オフセット・セグメントベース・消費バイト数を返す。
    // 構造の読み取り(ParseModRm)と実効アドレスの計算(EffectiveAddress)は IR と共有する。
    static private (bool isMem, uint offset, uint segBase, int inc) EnvGetMemOrRegAddr_(EmuEnvironment env, CPU cpu, int mod, int rm)
    {
        // 32ビットコードではデフォルトが32ビットModRMになり、address_size_prefix(0x67)で反転する
        var a32 = cpu.code32 != cpu.address_size_prefix;
        var modrm = ParseModRm(CodeSource.At(env, cpu), 0, (byte)((mod << 6) | rm), a32);
        if (modrm.Mod == 3)
            return (false, modrm.Rm, 0, 0);
        var (segment_base, ss_base) = EnvSegBases(cpu);
        var (isMem, offset, segBase) = EffectiveAddress(cpu, modrm, a32, segment_base, ss_base);
        return (isMem, offset, segBase, modrm.Length - 1);
    }

    // 物理アドレス(セグメントベース + 実効オフセット)を返す通常版。
    static public State<MemAddr> GetMemOrRegAddr(int mod, int rm) =>
        (env, cpu, opcodes) =>
        {
            var data = EnvGetMemOrRegAddr_(env, cpu, mod, rm);
            cpu.eip += (uint)data.inc;
            return (true, (data.isMem, data.offset + data.segBase), cpu, string.Empty);
        };

    // 実効オフセットのみを返す版(LEA 用: セグメントベースを加算しない)。
    static public State<MemAddr> GetMemOrRegOffset(int mod, int rm) =>
        (env, cpu, opcodes) =>
        {
            var data = EnvGetMemOrRegAddr_(env, cpu, mod, rm);
            cpu.eip += (uint)data.inc;
            return (true, (data.isMem, data.offset), cpu, string.Empty);
        };
}
