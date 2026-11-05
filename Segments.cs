using static Emu86.States;
using static Emu86.Mmu;
using static Emu86.DataAccess;
using static Emu86.CpuStates;

namespace Emu86;

// GDT の記述子(8 バイト。Low = 下位 dword、High = 上位 dword)。ビットの位置はここだけに書く。
//   Low  : リミット 15:0、基底 15:0
//   High : 基底 23:16、種別(bit8-11)、S(bit12)、DPL(bit13-14)、P(bit15)、リミット 19:16、D/B(bit22)、G(bit23)、基底 31:24
readonly record struct Descriptor(uint Low, uint High)
{
    public uint Base => (Low >> 16) | ((High & 0xFF) << 16) | (High & 0xFF000000);

    // リミット(G ビットが立っていれば 4K 単位を展開する)。
    public uint Limit => Granular ? (RawLimit << 12) | 0xFFF : RawLimit;
    uint RawLimit => (Low & 0xFFFF) | (High & 0x000F0000);
    bool Granular => (High & 0x00800000) != 0;

    // LAR が返すアクセス権(種別・S・DPL・P と、リミット 19:16・AVL・D/B・G)。
    public uint AccessRights => High & 0x00FFFF00;

    // D/B ビット: コードなら 32 ビットコード、スタックなら 32 ビットスタック。
    public bool Default32 => (High & 0x00400000) != 0;

    public int Dpl => (int)(High >> 13) & 3;

    // S ビット: コード/データのセグメント(落ちていればシステム記述子)。
    public bool IsSegment => (High & 0x1000) != 0;
    public bool IsCode => IsSegment && (High & 0x0800) != 0;
    public bool Conforming => IsCode && (High & 0x0400) != 0;

    // コードなら読み出し可、データなら書き込み可。
    public bool ReadableOrWritable => (High & 0x0200) != 0;

    // システム記述子の種別: TSS(1, 3, 9, 11)、LDT(2)、コールゲート(4, 12)、タスクゲート(5)。
    int SystemType => (int)(High >> 8) & 0x0F;
    public bool IsTssOrLdt => !IsSegment && SystemType is 1 or 2 or 3 or 9 or 11;
    public bool IsCallOrTaskGate => !IsSegment && SystemType is 4 or 5 or 12;
}

// IDT のゲート(8 バイト)。Low: 飛び先のオフセット 15:0 とセレクタ。High: 種別(bit8-11)、DPL(bit13-14)、オフセット 31:16。
readonly record struct Gate(uint Low, uint High)
{
    public uint Offset => (High & 0xFFFF0000) | (Low & 0xFFFF);
    public ushort Selector => (ushort)(Low >> 16);
    public int Dpl => (int)(High >> 13) & 3;

    // 割り込みゲート(種別 0xE)。トラップゲートは 0xF。
    public bool IsInterruptGate => ((High >> 8) & 0xF) == 0xE;
}

// セグメント記述子の読み取り・照合と、セグメントレジスタのロード。
static public class Segments
{
    // GDT の記述子を読む(範囲や種別は検査しない: セグメントレジスタのロードと TSS の参照)。
    static internal Descriptor EnvReadDescriptor(EmuEnvironment env, CPU cpu, ushort sel)
    {
        var offset = cpu.gdt_base + (uint)(sel & 0xFFF8);
        return new(EnvGetMemoryData32(env, offset), EnvGetMemoryData32(env, offset + 4));
    }

    // 記述子の照合(VERR/VERW/LAR/LSL)で見る記述子。ヌルセレクタ、LDT(未対応)、GDT の範囲外は null。
    static Descriptor? EnvFindDescriptor(EmuEnvironment env, CPU cpu, ushort sel) =>
        (sel & 0xFFFC) == 0 || (sel & 4) != 0 || (sel & 0xFFF8) + 7 > cpu.gdt_limit ? null : EnvReadDescriptor(env, cpu, sel);

    // CPL と RPL から届く特権か。コンフォーミングのコードは常に届き、それ以外は DPL >= CPL かつ DPL >= RPL が要る。
    static bool Reachable(Descriptor descriptor, CPU cpu, ushort sel) =>
        descriptor.Conforming || (descriptor.Dpl >= cpu.cpl && descriptor.Dpl >= (sel & 3));

    // VERR/VERW: セレクタのセグメントが CPL と RPL から読める(書ける)か。システム記述子は否。読みは実行専用のコードが否、
    // 書きはコードと読み取り専用のデータが否。例外は出さず真偽だけ返す。
    static internal bool EnvSegmentAccessible(EmuEnvironment env, CPU cpu, ushort sel, bool write) =>
        EnvFindDescriptor(env, cpu, sel) is { IsSegment: true } descriptor
        && (write ? !descriptor.IsCode && descriptor.ReadableOrWritable : !descriptor.IsCode || descriptor.ReadableOrWritable)
        && Reachable(descriptor, cpu, sel);

    // LAR/LSL: アクセス権またはリミット。照合に通らなければ null。
    // 許されるシステム記述子は LAR が TSS/LDT/コールゲート/タスクゲート、LSL が TSS/LDT。
    static internal uint? EnvDescriptorQuery(EmuEnvironment env, CPU cpu, ushort sel, bool limit) =>
        EnvFindDescriptor(env, cpu, sel) is { } descriptor
        && (descriptor.IsSegment || descriptor.IsTssOrLdt || (!limit && descriptor.IsCallOrTaskGate))
        && Reachable(descriptor, cpu, sel)
            ? limit ? descriptor.Limit : descriptor.AccessRights
            : null;

    // セグメントレジスタへのロード。
    // リアルモードは セレクタ*16 を基底に(セッタが自動設定)、
    // プロテクトモードは GDT 記述子から基底を読む。CS の場合は D ビットで code32 を更新する。
    static public State<Unit> LoadSReg(int reg, ushort sel) =>
        from _1 in ArraySreg[reg].Set(sel)                                           // セレクタ(リアルモードの基底 セレクタ*16 はセッタが置く)
        from descriptors in GetDataFromCpu(cpu => cpu.pe && !cpu.vm)                 // V86 モードはリアルモードと同じく セレクタ*16
        from descriptor in descriptors ? GetDataFromEnvCpu((env, cpu) => EnvReadDescriptor(env, cpu, sel)) : default(Descriptor).ToState()
        from _2 in When(descriptors, ArraySregBase[reg].Set(descriptor.Base))
        from _3 in When(reg == 1, Effect(cpu => cpu.code32 = descriptor.Default32))  // CS: D ビットで 32 ビットコードか決まる(リアルモードは 16 ビット)
        from _4 in When(reg == 2, Effect(cpu => cpu.stack32 = descriptor.Default32)) // SS: B ビットで 32 ビットスタックか決まる
        select Unit.unit;

    // MOV Sreg, r/m は常に下位16ビットのみロードする
    static public State<Unit> SetSReg3(int reg, Data data) =>
        LoadSReg(reg, data.type == 2 ? (ushort)data.dd : data.dw);
}
