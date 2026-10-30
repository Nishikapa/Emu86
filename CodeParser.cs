using System.Runtime.CompilerServices;
using static Emu86.Operands;

namespace Emu86;

// 命令バイト列の読み口。CS の基底と (E)IP からの相対位置でフェッチする。16 ビットコードセグメントでは
// IP(下位 16 ビット)で折り返す。読み出しは EnvGetMemoryData* 経由(ページング変換と #PF を含む)で、
// 必要なバイトだけをその場で読む(ページ境界のフォルトを余計に起こさない)。
public readonly struct CodeSource(EmuEnvironment env, uint csBase, uint eip, bool code32)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Address(int offset) => csBase + (code32 ? eip + (uint)offset : (ushort)(eip + offset));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Byte(int offset) => Mmu.EnvGetMemoryData8(env, Address(offset));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Word(int offset) => Mmu.EnvGetMemoryData16(env, Address(offset));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Dword(int offset) => Mmu.EnvGetMemoryData32(env, Address(offset));

    // 現在の CS:(E)IP から。
    public static CodeSource At(EmuEnvironment env, CPU cpu) => new(env, cpu.cs_base, cpu.eip, cpu.code32);
}

// 命令バイト列のパーサ。pos から読み、成功なら値と次の位置を返す(失敗は位置を進めない)。
// 読み出し自体は CodeSource に委ね、CPU 状態は変更しない。
public delegate (bool ok, T value, int next) Parser<T>(in CodeSource code, int pos);

// 命令プレフィックス(0 個以上)。F2/F3(REP)はこの CPU では命令として扱うので含めない。
[Flags]
public enum Prefix : ushort
{
    None = 0,
    Es = 1, Cs = 2, Ss = 4, Ds = 8, Fs = 16, Gs = 32,
    OperandSize = 64, AddressSize = 128, Lock = 256,
}

// ModRM バイトと、それに続く SIB・変位の構造。実効アドレスの計算は EffectiveAddress が行う。
public readonly struct ModRm(byte mod, byte reg, byte rm, bool hasSib, byte scale, byte index, byte baseRegister, uint disp, int length)
{
    public readonly byte Mod = mod, Reg = reg, Rm = rm;
    public readonly bool HasSib = hasSib;
    public readonly byte Scale = scale, Index = index, Base = baseRegister;
    // 変位。8 ビットは符号拡張済み、16 ビットはそのまま(実効アドレスは 16 ビットで折り返す)。
    public readonly uint Disp = disp;
    // ModRM バイトを含む消費バイト数。
    public readonly int Length = length;
}

static public partial class Decoder
{
    /// Parser コンビネータ ///////////////////////////////
    // Select/Where/Fold で組んだパーサは、組み立てを static に 1 度だけ行えば評価時に割り当てない。
    // SelectMany(クエリ構文の 2 つ目以降の from)は評価のたびにクロージャを作るので、毎命令の経路では使わない。
    static public Parser<T> ToParser<T>(this T value) => (in CodeSource code, int pos) => (true, value, pos);

    static public Parser<B> Select<A, B>(this Parser<A> parser, Func<A, B> selector) => (in CodeSource code, int pos) =>
    {
        var (ok, value, next) = parser(code, pos);
        return (ok, ok ? selector(value) : default, next);
    };

    static public Parser<C> SelectMany<A, B, C>(this Parser<A> parser, Func<A, Parser<B>> selector, Func<A, B, C> projector) => (in CodeSource code, int pos) =>
    {
        var (ok, a, next) = parser(code, pos);
        if (!ok) return (false, default, pos);
        var (ok2, b, next2) = selector(a)(code, next);
        return ok2 ? (true, projector(a, b), next2) : (false, default, pos);
    };

    static public Parser<T> Where<T>(this Parser<T> parser, Func<T, bool> predicate) => (in CodeSource code, int pos) =>
    {
        var (ok, value, next) = parser(code, pos);
        return ok && predicate(value) ? (true, value, next) : (false, default, pos);
    };

    static public Parser<T> Or<T>(this Parser<T> first, Parser<T> second) => (in CodeSource code, int pos) =>
    {
        var result = first(code, pos);
        return result.ok ? result : second(code, pos);
    };

    // 当てはまる限り繰り返して畳み込む(0 回以上。リストを作らない)。
    static public Parser<A> Fold<T, A>(this Parser<T> parser, A seed, Func<A, T, A> combine) => (in CodeSource code, int pos) =>
    {
        var accumulated = seed;
        while (true)
        {
            var (ok, value, next) = parser(code, pos);
            if (!ok) return (true, accumulated, pos);
            accumulated = combine(accumulated, value);
            pos = next;
        }
    };

    static public readonly Parser<byte> Fetch8 = (in CodeSource code, int pos) => (true, code.Byte(pos), pos + 1);
    static public readonly Parser<ushort> Fetch16 = (in CodeSource code, int pos) => (true, code.Word(pos), pos + 2);
    static public readonly Parser<uint> Fetch32 = (in CodeSource code, int pos) => (true, code.Dword(pos), pos + 4);

    /// プレフィックス ///////////////////////////////
    static public Prefix PrefixOf(byte value) => value switch
    {
        0x26 => Prefix.Es,
        0x2E => Prefix.Cs,
        0x36 => Prefix.Ss,
        0x3E => Prefix.Ds,
        0x64 => Prefix.Fs,
        0x65 => Prefix.Gs,
        0x66 => Prefix.OperandSize,
        0x67 => Prefix.AddressSize,
        0xF0 => Prefix.Lock, // 単一 CPU なのでアトミック性は自明。消費するだけ。
        _ => Prefix.None,
    };

    // 0 個以上のプレフィックスを旗の集合に畳み込み、消費したバイト数を返す。
    // 毎命令の先頭で呼ばれるので、コンビネータ(Fetch8.Select(PrefixOf).Where(...).Fold(...))と同じ意味の
    // 直接のループにしてデリゲート呼び出しを避ける。Parser としては Prefixes が同じ結果を返す。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static public int ScanPrefixes(in CodeSource code, int pos, out Prefix prefixes)
    {
        prefixes = Prefix.None;
        var length = 0;
        while (true)
        {
            var prefix = PrefixOf(code.Byte(pos + length));
            if (prefix == Prefix.None) return length;
            prefixes |= prefix;
            length++;
        }
    }

    static public readonly Parser<Prefix> Prefixes = (in CodeSource code, int pos) =>
    {
        var length = ScanPrefixes(code, pos, out var prefixes);
        return (true, prefixes, pos + length);
    };

    // セグメントオーバーライドの基底。複数あるときは ES > CS > SS > DS > FS > GS の優先順、無ければ null。
    static public uint? OverrideBase(Prefix prefixes, CPU cpu) =>
        (prefixes & Prefix.Es) != 0 ? cpu.es_base :
        (prefixes & Prefix.Cs) != 0 ? cpu.cs_base :
        (prefixes & Prefix.Ss) != 0 ? cpu.ss_base :
        (prefixes & Prefix.Ds) != 0 ? cpu.ds_base :
        (prefixes & Prefix.Fs) != 0 ? cpu.fs_base :
        (prefixes & Prefix.Gs) != 0 ? cpu.gs_base : null;

    /// ModRM ///////////////////////////////
    // ModRM バイト(値は modrm)の残り(SIB と変位)を pos から読む。pos は ModRM バイトの次の位置。
    // 16 ビット: mod=0,rm=6 は [disp16]。32 ビット: rm=4 は SIB、mod=0,rm=5(SIB では base=5)は [disp32]。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static public ModRm ParseModRm(in CodeSource code, int pos, byte modrm, bool a32)
    {
        var mod = (byte)(modrm >> 6);
        var reg = (byte)((modrm >> 3) & 7);
        var rm = (byte)(modrm & 7);
        if (mod == 3)
            return new(mod, reg, rm, false, 0, 0, 0, 0, 1);
        if (!a32)
        {
            if (mod == 0 && rm == 6)
                return new(mod, reg, rm, false, 0, 0, 0, code.Word(pos), 3);
            return mod switch
            {
                0 => new(mod, reg, rm, false, 0, 0, 0, 0, 1),
                1 => new(mod, reg, rm, false, 0, 0, 0, (uint)(sbyte)code.Byte(pos), 2),
                _ => new(mod, reg, rm, false, 0, 0, 0, code.Word(pos), 3),
            };
        }
        if (rm == 4)
        {
            var sib = code.Byte(pos);
            var scale = (byte)(sib >> 6);
            var index = (byte)((sib >> 3) & 7);
            var baseRegister = (byte)(sib & 7);
            if (mod == 0 && baseRegister == 5)
                return new(mod, reg, rm, true, scale, index, baseRegister, code.Dword(pos + 1), 6);
            return mod switch
            {
                0 => new(mod, reg, rm, true, scale, index, baseRegister, 0, 2),
                1 => new(mod, reg, rm, true, scale, index, baseRegister, (uint)(sbyte)code.Byte(pos + 1), 3),
                _ => new(mod, reg, rm, true, scale, index, baseRegister, code.Dword(pos + 1), 6),
            };
        }
        if (mod == 0 && rm == 5)
            return new(mod, reg, rm, false, 0, 0, 0, code.Dword(pos), 5);
        return mod switch
        {
            0 => new(mod, reg, rm, false, 0, 0, 0, 0, 1),
            1 => new(mod, reg, rm, false, 0, 0, 0, (uint)(sbyte)code.Byte(pos), 2),
            _ => new(mod, reg, rm, false, 0, 0, 0, code.Dword(pos), 5),
        };
    }

    // ModRM バイトから読む Parser(構造だけを返す)。
    static public Parser<ModRm> ModRmParser(bool a32) => a32 ? ModRm32 : ModRm16;

    static readonly Parser<ModRm> ModRm16 = (in CodeSource code, int pos) =>
    {
        var modrm = ParseModRm(code, pos + 1, code.Byte(pos), false);
        return (true, modrm, pos + modrm.Length);
    };

    static readonly Parser<ModRm> ModRm32 = (in CodeSource code, int pos) =>
    {
        var modrm = ParseModRm(code, pos + 1, code.Byte(pos), true);
        return (true, modrm, pos + modrm.Length);
    };

    // 実効アドレス: (メモリか, オフセット, セグメント基底)。レジスタ形式(mod=3)はオフセットにレジスタ番号を返す。
    // segBase は既定 DS(オーバーライドで置換済み)、ssBase は BP/EBP/ESP ベースの既定 SS(同じく置換済み)。
    // 16 ビットアドレッシングの実効アドレスは 16 ビットで折り返す(BP に負の変位を足して桁借りする場合など)。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static public (bool isMem, uint offset, uint segBase) EffectiveAddress(CPU cpu, in ModRm m, bool a32, uint segBase, uint ssBase)
    {
        if (m.Mod == 3)
            return (false, m.Rm, 0);
        if (!a32)
        {
            if (m.Mod == 0 && m.Rm == 6) // [disp16]
                return (true, m.Disp, segBase);
            uint regsum = m.Rm switch
            {
                0 => (ushort)(cpu.bx + cpu.si),
                1 => (ushort)(cpu.bx + cpu.di),
                2 => (ushort)(cpu.bp + cpu.si),
                3 => (ushort)(cpu.bp + cpu.di),
                4 => cpu.si,
                5 => cpu.di,
                6 => cpu.bp,
                _ => cpu.bx,
            };
            var segment16 = m.Rm is 2 or 3 or 6 ? ssBase : segBase;
            return (true, m.Mod == 0 ? regsum : (ushort)(regsum + m.Disp), segment16);
        }
        if (m.HasSib)
        {
            var scaled = (uint)((1 << m.Scale) * EnvGetIndexRegData32(cpu, m.Index));
            if (m.Mod == 0 && m.Base == 5) // ベースなし + disp32
                return (true, scaled + m.Disp, segBase);
            var segmentSib = m.Base is 4 or 5 ? ssBase : segBase;
            return (true, scaled + ReadRegister32(cpu, m.Base) + m.Disp, segmentSib);
        }
        if (m.Mod == 0 && m.Rm == 5) // [disp32]
            return (true, m.Disp, segBase);
        var segment32 = m.Rm == 5 ? ssBase : segBase;
        return (true, ReadRegister32(cpu, m.Rm) + m.Disp, segment32);
    }
}
