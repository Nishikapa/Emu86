namespace Emu86;

public partial class CPU
{
    // 保存形式(v2 から不変)。導出プロパティ(ip/bp/sp/al/ah 等)は元の 32bit レジスタから再計算されるため書き出さない。
    // セグメントは _cs 等のアクセサで読み書きする(セッタが置く セレクタ*16 の基底は、直後の *_base で上書きされる)。
    // アクセサは別ファイルの static フィールドなので、初期化順に依存しないよう最初の利用時に組み立てる。
    static Codec<CPU> registers;
    static Codec<CPU> Registers => registers ??= new Codec<CPU>()
        .U16(_cs).U16(_ds).U16(_es).U16(_ss).U16(_fs).U16(_gs)
        .U32(_cs_base).U32(_ds_base).U32(_es_base).U32(_ss_base).U32(_fs_base).U32(_gs_base)
        .U32(_eip)
        .Bool(c => c.code32, (c, v) => c.code32 = v)
        .Bool(c => c.stack32, (c, v) => c.stack32 = v)
        .U16(c => c.idt_limit, (c, v) => c.idt_limit = v).U32(c => c.idt_base, (c, v) => c.idt_base = v)
        .U16(c => c.gdt_limit, (c, v) => c.gdt_limit = v).U32(c => c.gdt_base, (c, v) => c.gdt_base = v)
        .U32(_cr0).U32(_cr2).U32(_cr3).U32(_cr4)
        .U16(c => c.fpu_cw, (c, v) => c.fpu_cw = v).U16(c => c.fpu_sw, (c, v) => c.fpu_sw = v)
        .U32(_ebp).U32(_esp)
        .U32(c => c.eflags, (c, v) => c.eflags = v)
        .Bool(_cs_prefix).Bool(_es_prefix).Bool(_ss_prefix).Bool(_ds_prefix)
        .Bool(_fs_prefix).Bool(_gs_prefix).Bool(_operand_size_prefix).Bool(_address_size_prefix)
        .U32(_eax).U32(_ebx).U32(_ecx).U32(_edx).U32(_esi).U32(_edi);

    // v5 で FPU スタック、v6 でタスク/LDT レジスタを追加。
    static Codec<CPU> runtime;
    static Codec<CPU> Runtime => runtime ??= new Codec<CPU>()
        .I32(c => c.fpu_top, (c, v) => c.fpu_top = v, since: 5)
        .Check(c => c.fpu_top is >= 0 and <= 7, "Invalid FPU stack top")
        .U8(c => c.fpu_valid, (c, v) => c.fpu_valid = v, since: 5)
        .F64s(c => c.fpu_st, since: 5)
        .U16(c => c.tr, (c, v) => c.tr = v, since: 6)
        .U16(c => c.ldtr, (c, v) => c.ldtr = v, since: 6);

    public void WriteTo(BinaryWriter writer) => Registers.Save(writer, this);

    public static CPU ReadFrom(BinaryReader reader)
    {
        var cpu = new CPU();
        Registers.Load(reader, cpu, SnapshotStore.Version);
        return cpu;
    }

    internal void SaveRuntimeState(BinaryWriter writer) => Runtime.Save(writer, this);

    internal void LoadRuntimeState(BinaryReader reader, int version)
    {
        Runtime.Load(reader, this, version);
        Decode = default;
    }
}
