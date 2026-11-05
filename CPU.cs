namespace Emu86;

// O の中の項目 P の読み書きの対(getter と、書き込んだ O を返す setter)。CPU のレジスタ・フラグ・プレフィックスのアクセサがこの形。
public class Accessor<O, P>(Func<O, P> g, Func<O, Func<P, O>> s)
{
    public Func<O, P> getter { get; set; } = g;
    public Func<O, Func<P, O>> setter { get; set; } = s;
}

// CPU 状態。参照型(class)であることに意味がある:
// State モナドの各バインドを CPU の実体コピーではなく参照の受け渡しで通すことで、
// 1命令あたり数十回発生していた大きな構造体コピーをなくしている。
// アクセサの setter は同一インスタンスをその場で書き換えて返す。
public partial class CPU
{
    // セグメントレジスタのセッタは、リアルモードの基底(セレクタ*16)もあわせて更新する。
    // プロテクトモードでは LoadSReg が GDT 記述子から読んだ基底で上書きする(記述子キャッシュ相当)。
    static public readonly Accessor<CPU, ushort> _cs = new(c => c.cs, c => v => { c.cs = v; c.cs_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _ds = new(c => c.ds, c => v => { c.ds = v; c.ds_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _es = new(c => c.es, c => v => { c.es = v; c.es_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _ss = new(c => c.ss, c => v => { c.ss = v; c.ss_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _fs = new(c => c.fs, c => v => { c.fs = v; c.fs_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _gs = new(c => c.gs, c => v => { c.gs = v; c.gs_base = (uint)v << 4; return c; });

    public ushort cs { get; private set; }
    public ushort ds { get; private set; }
    public ushort es { get; private set; }
    public ushort ss { get; private set; }
    public ushort fs { get; private set; }
    public ushort gs { get; private set; }

    // 各セグメントの基底物理アドレス(記述子キャッシュ)。
    public uint cs_base, ds_base, es_base, ss_base, fs_base, gs_base;

    static public readonly Accessor<CPU, uint> _cs_base = new(c => c.cs_base, c => v => { c.cs_base = v; return c; });
    static public readonly Accessor<CPU, uint> _ds_base = new(c => c.ds_base, c => v => { c.ds_base = v; return c; });
    static public readonly Accessor<CPU, uint> _es_base = new(c => c.es_base, c => v => { c.es_base = v; return c; });
    static public readonly Accessor<CPU, uint> _ss_base = new(c => c.ss_base, c => v => { c.ss_base = v; return c; });
    static public readonly Accessor<CPU, uint> _fs_base = new(c => c.fs_base, c => v => { c.fs_base = v; return c; });
    static public readonly Accessor<CPU, uint> _gs_base = new(c => c.gs_base, c => v => { c.gs_base = v; return c; });

    static public readonly Accessor<CPU, ushort> _ip = new(c => c.ip, c => v => { c.ip = v; return c; });

    static public readonly Accessor<CPU, uint> _eip = new(c => c.eip, c => v => { c.eip = v; return c; });

    // 翻訳された IR(解釈器とコンパイル済みコード)が直接書き込むため、セッターは公開している。
    public uint eip { get; set; }
    public ushort ip { get => (ushort)(this.eip & 0xFFFF); private set => this.eip = (this.eip & 0xFFFF0000) + value; }

    // プロテクトモードで32ビットコードセグメント(D=1)を実行中かどうか。
    // CSがプロテクトモードでロードされたときにセットされる。
    public bool code32;

    // 直前の命令が HLT だった(ランナーが見て、配送できる割り込みが無ければ次の割り込みまで仮想時間を進める)。
    // 過渡状態なのでスナップショットには含めない。
    public bool Halted;

    // スタックが32ビット(SS の B ビット=1)かどうか。スタック操作(PUSH/POP/CALL/RET/
    // 割り込み)で SP/ESP のどちらを使うかは CS.D ではなく SS.B で決まる。
    // 例: PnP BIOS は 16ビットCS + 32ビットSS で動くため両者が食い違う。
    public bool stack32;
    // タスクレジスタ / LDT レジスタのセレクタ。タスク切替や LDT 自体は未実装だが、
    // LTR/LLDT で保持し STR/SLDT で返す(NT カーネルは STR→GDT から TSS の番地を求める)。
    public ushort tr;
    public ushort ldtr;

    // x87 FPU の状態。ST(i) = fpu_st[(fpu_top + i) & 7]。
    // 値は C# の double で保持する(実機の内部 80 ビットではなく倍精度)。
    // fpu_sw のうち TOP フィールド(bit11-13)は fpu_top から合成する(FpuSw)。
    // fpu_valid は物理レジスタごとの「空でない」ビットマスク(タグワードの元)。
    public ushort fpu_cw = 0x037F;
    public ushort fpu_sw;
    public double[] fpu_st = new double[8];
    public int fpu_top;
    public byte fpu_valid;

    public ushort idt_limit { get; set; }
    public uint idt_base { get; set; }
    public ushort gdt_limit { get; set; }
    public uint gdt_base { get; set; }

    static public readonly Accessor<CPU, uint> _cr0 = new(c => c.cr0, c => v => { c.cr0 = v; return c; });
    static public readonly Accessor<CPU, uint> _cr2 = new(c => c.cr2, c => v => { c.cr2 = v; return c; });
    static public readonly Accessor<CPU, uint> _cr3 = new(c => c.cr3, c => v => { c.cr3 = v; return c; });
    static public readonly Accessor<CPU, uint> _cr4 = new(c => c.cr4, c => v => { c.cr4 = v; return c; });

    private uint cr0 { get; set; }
    public uint cr2 { get; set; }          // #PF 発生時にフォルトした線形アドレスを保持する
    public uint cr3 { get; private set; }  // Paging.Sync がページテーブル基底として参照する
    public uint cr4 { get; private set; }  // Paging.Sync が PAE ビットを参照する

    static public readonly Accessor<CPU, ushort> _sp = new(c => c.sp, c => v => { c.sp = v; return c; });
    static public readonly Accessor<CPU, ushort> _bp = new(c => c.bp, c => v => { c.bp = v; return c; });

    public ushort bp { get => (ushort)(this.ebp & 0xFFFF); set => this.ebp = (this.ebp & 0xFFFF0000) + value; }
    public ushort sp { get => (ushort)(this.esp & 0xFFFF); set => this.esp = (this.esp & 0xFFFF0000) + value; }

    static public readonly Accessor<CPU, uint> _esp = new(c => c.esp, c => v => { c.esp = v; return c; });
    static public readonly Accessor<CPU, uint> _ebp = new(c => c.ebp, c => v => { c.ebp = v; return c; });

    public uint ebp { get; set; }
    public uint esp { get; set; }

    // EFLAGS。bit1 は実 CPU では常に 1(PUSHF/トラップフレーム経由で見える。難読化コードが
    // フラグ値を演算に使うことがあるため、読み書きの両方で強制する)。
    public uint eflags { get => field | 2; set => field = value | 2; } = 2;
    static public readonly Accessor<CPU, uint> _eflags = new(c => c.eflags, c => v => { c.eflags = v; return c; });

    private const uint CF = 1;
    private const uint PF = 4;
    private const uint AF = 0x10;
    private const uint ZF = 0x40;
    private const uint SF = 0x80;
    private const uint TF = 0x100;
    private const uint JF = 0x200;
    private const uint DF = 0x400;
    private const uint OF = 0x800;

    private void UpdateEflags(uint flag, bool f)
    {
        this.eflags = (this.eflags & ~flag) | (f ? flag : 0);
    }

    private bool GetEflags(uint flag) => 0 != (this.eflags & flag);

    public bool pe => 0 != (this.cr0 & 0x1);
    // 仮想 8086 モード(EFLAGS.VM)。セグメントはリアルモード同様 セレクタ*16 が基底で、特権レベルは常に 3。
    public bool vm => 0 != (this.eflags & 0x20000);
    // 現在の特権レベル。V86 モードでは CS の RPL に関わらず 3。
    public int cpl => this.vm ? 3 : this.cs & 3;

    // ページング有効(CR0.PG)。変換に使う実際の状態は EmuEnvironment 側に
    // ミラーされる(Paging.Sync)。CR 書き込みとスナップショット復元時に同期する。
    public bool pg => 0 != (this.cr0 & 0x80000000u);

    // 書き込み保護(CR0.WP)。リング0 でも読み取り専用ページへの書き込みを #PF にする。
    public bool wp => 0 != (this.cr0 & 0x10000);

    public DecodeContext Decode;
    public bool cs_prefix { get => Decode.Cs; set => Decode.Cs = value; }
    public bool es_prefix { get => Decode.Es; set => Decode.Es = value; }
    public bool ss_prefix { get => Decode.Ss; set => Decode.Ss = value; }
    public bool ds_prefix { get => Decode.Ds; set => Decode.Ds = value; }
    public bool fs_prefix { get => Decode.Fs; set => Decode.Fs = value; }
    public bool gs_prefix { get => Decode.Gs; set => Decode.Gs = value; }
    public bool operand_size_prefix { get => Decode.OperandSize; set => Decode.OperandSize = value; }
    public bool address_size_prefix { get => Decode.AddressSize; set => Decode.AddressSize = value; }
    // LOCK (F0)。単一CPUなのでアトミック性は自明に満たされ、実質無視でよい。
    // 命令間では常に false のためスナップショットには保存しない。
    public bool lock_prefix { get => Decode.Lock; set => Decode.Lock = value; }
    static public readonly Accessor<CPU, bool> _cs_prefix = new(c => c.cs_prefix, c => v => { c.cs_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _es_prefix = new(c => c.es_prefix, c => v => { c.es_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _ss_prefix = new(c => c.ss_prefix, c => v => { c.ss_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _ds_prefix = new(c => c.ds_prefix, c => v => { c.ds_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _fs_prefix = new(c => c.fs_prefix, c => v => { c.fs_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _gs_prefix = new(c => c.gs_prefix, c => v => { c.gs_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _operand_size_prefix = new(c => c.operand_size_prefix, c => v => { c.operand_size_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _address_size_prefix = new(c => c.address_size_prefix, c => v => { c.address_size_prefix = v; return c; });

    public bool cf { get => GetEflags(CF); set => UpdateEflags(CF, value); }
    public bool pf { get => GetEflags(PF); set => UpdateEflags(PF, value); }
    public bool af { get => GetEflags(AF); set => UpdateEflags(AF, value); }
    public bool zf { get => GetEflags(ZF); set => UpdateEflags(ZF, value); }
    public bool sf { get => GetEflags(SF); set => UpdateEflags(SF, value); }
    public bool tf { get => GetEflags(TF); set => UpdateEflags(TF, value); }
    public bool jf { get => GetEflags(JF); set => UpdateEflags(JF, value); }
    public bool df { get => GetEflags(DF); set => UpdateEflags(DF, value); }
    public bool of { get => GetEflags(OF); set => UpdateEflags(OF, value); }

    static public readonly Accessor<CPU, bool> _cf = new(c => c.cf, c => v => { c.cf = v; return c; });
    static public readonly Accessor<CPU, bool> _pf = new(c => c.pf, c => v => { c.pf = v; return c; });
    static public readonly Accessor<CPU, bool> _zf = new(c => c.zf, c => v => { c.zf = v; return c; });
    static public readonly Accessor<CPU, bool> _sf = new(c => c.sf, c => v => { c.sf = v; return c; });
    static public readonly Accessor<CPU, bool> _tf = new(c => c.tf, c => v => { c.tf = v; return c; });
    static public readonly Accessor<CPU, bool> _jf = new(c => c.jf, c => v => { c.jf = v; return c; });
    static public readonly Accessor<CPU, bool> _df = new(c => c.df, c => v => { c.df = v; return c; });
    static public readonly Accessor<CPU, bool> _of = new(c => c.of, c => v => { c.of = v; return c; });

    static public readonly Accessor<CPU, byte> _al = new(c => c.al, c => v => { c.al = v; return c; });
    static public readonly Accessor<CPU, byte> _bl = new(c => c.bl, c => v => { c.bl = v; return c; });
    static public readonly Accessor<CPU, byte> _cl = new(c => c.cl, c => v => { c.cl = v; return c; });
    static public readonly Accessor<CPU, byte> _dl = new(c => c.dl, c => v => { c.dl = v; return c; });

    public byte al { get => (byte)(this.eax & 0xFF); set => this.eax = (this.eax & 0xFFFFFF00) + value; }
    public byte bl { get => (byte)(this.ebx & 0xFF); set => this.ebx = (this.ebx & 0xFFFFFF00) + value; }
    public byte cl { get => (byte)(this.ecx & 0xFF); set => this.ecx = (this.ecx & 0xFFFFFF00) + value; }
    public byte dl { get => (byte)(this.edx & 0xFF); set => this.edx = (this.edx & 0xFFFFFF00) + value; }

    static public readonly Accessor<CPU, byte> _ah = new(c => c.ah, c => v => { c.ah = v; return c; });
    static public readonly Accessor<CPU, byte> _bh = new(c => c.bh, c => v => { c.bh = v; return c; });
    static public readonly Accessor<CPU, byte> _ch = new(c => c.ch, c => v => { c.ch = v; return c; });
    static public readonly Accessor<CPU, byte> _dh = new(c => c.dh, c => v => { c.dh = v; return c; });

    public byte ah { get => (byte)((this.eax >> 8) & 0xFF); set => this.eax = (this.eax & 0xFFFF00FF) + ((uint)value << 8); }
    public byte bh { get => (byte)((this.ebx >> 8) & 0xFF); set => this.ebx = (this.ebx & 0xFFFF00FF) + ((uint)value << 8); }
    public byte ch { get => (byte)((this.ecx >> 8) & 0xFF); set => this.ecx = (this.ecx & 0xFFFF00FF) + ((uint)value << 8); }
    public byte dh { get => (byte)((this.edx >> 8) & 0xFF); set => this.edx = (this.edx & 0xFFFF00FF) + ((uint)value << 8); }

    static public readonly Accessor<CPU, ushort> _ax = new(c => c.ax, c => v => { c.ax = v; return c; });
    static public readonly Accessor<CPU, ushort> _bx = new(c => c.bx, c => v => { c.bx = v; return c; });
    static public readonly Accessor<CPU, ushort> _cx = new(c => c.cx, c => v => { c.cx = v; return c; });
    static public readonly Accessor<CPU, ushort> _dx = new(c => c.dx, c => v => { c.dx = v; return c; });

    public ushort ax { get => (ushort)(this.eax & 0xFFFF); set => this.eax = (this.eax & 0xFFFF0000) + value; }
    public ushort bx { get => (ushort)(this.ebx & 0xFFFF); set => this.ebx = (this.ebx & 0xFFFF0000) + value; }
    public ushort cx { get => (ushort)(this.ecx & 0xFFFF); set => this.ecx = (this.ecx & 0xFFFF0000) + value; }
    public ushort dx { get => (ushort)(this.edx & 0xFFFF); set => this.edx = (this.edx & 0xFFFF0000) + value; }

    static public readonly Accessor<CPU, uint> _eax = new(c => c.eax, c => v => { c.eax = v; return c; });
    static public readonly Accessor<CPU, uint> _ebx = new(c => c.ebx, c => v => { c.ebx = v; return c; });
    static public readonly Accessor<CPU, uint> _ecx = new(c => c.ecx, c => v => { c.ecx = v; return c; });
    static public readonly Accessor<CPU, uint> _edx = new(c => c.edx, c => v => { c.edx = v; return c; });

    public uint eax { get; set; }
    public uint ebx { get; set; }
    public uint ecx { get; set; }
    public uint edx { get; set; }

    static public readonly Accessor<CPU, ushort> _si = new(c => c.si, c => v => { c.si = v; return c; });
    static public readonly Accessor<CPU, ushort> _di = new(c => c.di, c => v => { c.di = v; return c; });

    public ushort si { get => (ushort)(this.esi & 0xFFFF); set => this.esi = (this.esi & 0xFFFF0000) + value; }
    public ushort di { get => (ushort)(this.edi & 0xFFFF); set => this.edi = (this.edi & 0xFFFF0000) + value; }

    static public readonly Accessor<CPU, uint> _esi = new(c => c.esi, c => v => { c.esi = v; return c; });
    static public readonly Accessor<CPU, uint> _edi = new(c => c.edi, c => v => { c.edi = v; return c; });

    public uint esi { get; set; }
    public uint edi { get; set; }

    public void CopyTo(CPU d)
    {
        d.cs = cs; d.ds = ds; d.es = es; d.ss = ss; d.fs = fs; d.gs = gs;
        d.cs_base = cs_base; d.ds_base = ds_base; d.es_base = es_base;
        d.ss_base = ss_base; d.fs_base = fs_base; d.gs_base = gs_base;
        d.eip = eip; d.code32 = code32; d.stack32 = stack32;
        d.tr = tr; d.ldtr = ldtr;
        d.idt_limit = idt_limit; d.idt_base = idt_base;
        d.gdt_limit = gdt_limit; d.gdt_base = gdt_base;
        d.cr0 = cr0; d.cr2 = cr2; d.cr3 = cr3; d.cr4 = cr4;
        d.fpu_cw = fpu_cw; d.fpu_sw = fpu_sw;
        d.fpu_top = fpu_top; d.fpu_valid = fpu_valid;
        Array.Copy(fpu_st, d.fpu_st, 8);
        d.ebp = ebp; d.esp = esp; d.eflags = eflags;
        d.eax = eax; d.ebx = ebx; d.ecx = ecx; d.edx = edx;
        d.esi = esi; d.edi = edi;
        d.Decode = Decode;
    }
}
