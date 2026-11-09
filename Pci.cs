namespace Emu86;

using System.Buffers.Binary;
using static Emu86.Writers;

// 最小限の PCI ホスト(コンフィグ機構 #1: ポート 0xCF8 CONFIG_ADDRESS / 0xCFC CONFIG_DATA)。
// Linux の ata_piix ドライバを bind させるだけの目的で、QEMU/Bochs 相当の
// i440FX + PIIX3(ISA ブリッジ + IDE)を bus 0 に露出する。
// IDE はレガシー(互換)モードにしてあり、BAR はすべて 0(BMDMA 無し)。
// そのため libata は既存の ATA PIO ポート(0x1F0/0x3F6)を PIO で叩く。
public class PciHost
{
    // 0xCF8 に書かれる CONFIG_ADDRESS ラッチ。bit31=enable, 23:16=bus, 15:11=dev, 10:8=fn, 7:2=reg。
    public uint Address;

    // --pcilog: コンフィグ空間アクセスを標準エラーへ記録する(OS の PCI 列挙手順の調査用)。
    public bool Log;
    public uint Eip; // ログ用: アクセス元の EIP(ランナーが命令ごとに更新)
    public Func<string> Caller = () => ""; // ログ用: EBP チェーンの戻り番地列(ランナーが設定)

    // 存在する各ファンクションの 256 バイト・コンフィグ空間。キー = dev<<3 | fn。
    readonly Dictionary<int, byte[]> cfg = new();

    // 実装済み BAR の書き込み可能ビットマスク(サイズ問い合わせ用)。キー = (fn key, レジスタオフセット)。
    // 未登録の BAR / 拡張 ROM BAR(0x30)は読み取り専用 0(未実装)。
    readonly Dictionary<(int fn, int off), uint> barMask = new();

    // スナップショット(v7 から): CONFIG_ADDRESS と、ファンクションごとのコンフィグ空間(キー順)。
    internal static readonly Codec<PciHost> Layout = new Codec<PciHost>()
        .U32(p => p.Address, (p, v) => p.Address = v)
        .Custom(
            (writer, p) =>
            {
                writer.Write(p.cfg.Count);
                foreach (var entry in p.cfg.OrderBy(entry => entry.Key))
                {
                    writer.Write(entry.Key);
                    writer.Write(entry.Value);
                }
            },
            (reader, p) =>
            {
                // 旧スナップショットに無いファンクション(後から追加したデバイス)は初期状態のままにする。
                var count = SnapshotStore.ReadCount(reader, 256);
                if (count > p.cfg.Count) throw new InvalidDataException("PCI topology differs from snapshot");
                var restored = new HashSet<int>();
                for (var index = 0; index < count; index++)
                {
                    var key = reader.ReadInt32();
                    if (!p.cfg.TryGetValue(key, out var config) || !restored.Add(key))
                        throw new InvalidDataException("Invalid PCI function in snapshot");
                    reader.BaseStream.ReadExactly(config);
                }
                // コンフィグ空間から導いて持っている値(線形フレームバッファの番地)を取り直す。環境のスナップショットはこの表を
                // 直接読むので、ここで行う(以前は LoadState だけが取り直していて、再開後は番地が 0 のままだった)。
                p.UpdateLfbBase();
            });

    public void SaveState(BinaryWriter writer) => Layout.Save(writer, this);

    public void LoadState(BinaryReader reader) => Layout.Load(reader, this, SnapshotStore.Version);

    // ファンクションのキー(dev<<3 | fn)。
    static int Key(int device, int function) => (device << 3) | function;

    const int IdeKey = (1 << 3) | 1;
    public const int VgaKey = 2 << 3;

    // コンフィグ空間の dword レジスタ(BAR など)。
    uint Register(int key, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(cfg[key].AsSpan(offset));

    // PIIX4 IDE のバスマスタ I/O ベース(BAR4)。SeaBIOS/OS が割り当て直す。
    public uint IdeBmBase => Register(IdeKey, 0x20) is var bar && (bar & 1) != 0 ? bar & 0xFFF0 : 0;

    // 各ファンクションのコンフィグ空間(Function: 共通ヘッダ + そのデバイスの項目。At はコンフィグ空間の先頭からの位置)。
    public PciHost()
    {
        // 00:00.0 Intel 440FX ホストブリッジ
        cfg[Key(0, 0)] = Function(0x8086, 0x1237, baseClass: 0x06, subClass: 0x00, progIf: 0x00, header: 0x00, revision: 0x02);
        // 00:01.0 PIIX4 ISA ブリッジ(マルチファンクション: header bit7=1 で fn1/fn3 も走査させる)
        // 00:01.1 PIIX4 IDE(mass storage / IDE / prog-if=0x80: 両チャネル互換モード + バスマスタ能力)
        // デバイス ID は PIIX3(7000/7010)ではなく PIIX4(7110/7111)にする。Windows XP のディスクイメージ
        // (Virtual PC 由来)の CriticalDeviceDatabase には pci#ven_8086&dev_7111 -> intelide しか無く、
        // 7010 だとブート時にドライバが結び付かず INACCESSIBLE_BOOT_DEVICE(0x7B)になるため。
        // SeaBIOS/Linux はどちらの ID でも同じ扱い(piix_isa_setup / piix_ide_setup / ata_piix)。
        // 割り込み: IDE はレガシー IRQ14/15 を使うため PCI INTx は使わない(pin=0)。
        cfg[Key(1, 0)] = Function(0x8086, 0x7110, baseClass: 0x06, subClass: 0x01, progIf: 0x00, header: 0x80, revision: 0x01);
        // IDE BAR4: バスマスタ IDE レジスタ(I/O、16 バイト)。Windows の pciidex は資源リストが
        // 空だと FDO の開始に失敗するため、レジスタ自体はほぼダミーでも BAR として露出する。
        cfg[IdeKey] = Function(0x8086, 0x7111, baseClass: 0x01, subClass: 0x01, progIf: 0x80, header: 0x00, revision: 0x01,
            At(0x20, U32(0xC001)));         // BAR4(bit0 = I/O 空間)
        barMask[(IdeKey, 0x20)] = 0xFFFFFFF0;
        // 00:01.3 PIIX4 電源管理(ACPI)。SeaBIOS はこのデバイスを見つけたときだけ
        // ACPI テーブル(RSDP/RSDT/FADT/DSDT…)を生成する。Windows の ACPI HAL(halacpi)は
        // ACPI テーブルが無いと MISMATCHED_HAL(0x79) でバグチェックするため必須。
        // PM I/O ブロックは 0xB000、SMBus は 0xB100(QEMU の PIIX4 と同じ配置)。
        // DEVACTB(0x58) bit25 = APMC_EN。SeaBIOS はこれが立っていると「SMM 初期化済み」と
        // みなして SMM の再配置(APM ポート 0xB2/0xB3 経由の SMI 待ち)を省略する。
        // SMM は未実装なので、待ちに入ると永久にループするため最初から立てておく。
        cfg[Key(1, 3)] = Function(0x8086, 0x7113, baseClass: 0x06, subClass: 0x80, progIf: 0x00, header: 0x00, revision: 0x03,
            At(0x3D, U8(0x01)),             // 割り込みピン INTA(SCI)
            At(0x40, U32(0xB001)),          // PMBA(bit0 = I/O 空間)
            At(0x58, U32(0x02000000)),      // DEVACTB: APMC_EN
            At(0x80, U8(0x01)),             // PMREGMISC: PM I/O 有効
            At(0x90, U32(0xB101)),          // SMBBA
            At(0xD2, U8(0x09)));            // SMBus ホスト有効
        // 00:02.0 VGA 互換ディスプレイ(QEMU の標準 VGA と同じ 1234:1111)。SeaBIOS はこのクラスのデバイスを
        // 見つけると拡張 ROM BAR(0x30)から VGA BIOS を 0xC0000 へ取り込んで実行し、画面出力を有効にする。
        // ROM は VgaBiosStub(64KB 窓、実体は 2KB)。BAR0 は Bochs VBE の線形フレームバッファ(16MB、プリフェッチ可能なメモリ空間。
        // SeaBIOS/OS が番地を割り当て、Environment は RAM 外のアクセスをこの範囲で VgaDevice.Lfb へ振り分ける)。
        // XP の vga.sys は旧来の VGA 窓を使い、Linux の bochs_drm はこの BAR を使う。
        cfg[VgaKey] = Function(0x1234, 0x1111, baseClass: 0x03, subClass: 0x00, progIf: 0x00, header: 0x00, revision: 0x02,
            At(0x10, U32(0x00000008)));     // BAR0(bit3 = プリフェッチ可能)
        barMask[(VgaKey, 0x10)] = 0xFF000000; // 16MB
        barMask[(VgaKey, 0x30)] = 0xFFFF0001; // 64KB、bit0 = ROM デコード有効
    }

    // VGA の BAR0(線形フレームバッファ)の物理アドレス(0 = 未割り当て)。コンフィグ書き込みと状態の読み込みで更新する。
    public uint LfbBase { get; private set; }

    void UpdateLfbBase() => LfbBase = Register(VgaKey, 0x10) & 0xFF000000;

    // VGA の拡張 ROM BAR が有効なら、その 64KB 窓の物理アドレス範囲に VgaBiosStub.Rom を見せる。
    public bool RomContains(uint physical) =>
        Register(VgaKey, 0x30) is var bar && (bar & 1) != 0 && physical - (bar & 0xFFFF0000) < 0x10000;

    public byte RomRead(uint physical)
    {
        var offset = physical & 0xFFFF;
        return offset < VgaBiosStub.Rom.Length ? VgaBiosStub.Rom[offset] : (byte)0xFF;
    }

    // コンフィグ空間(256 バイト)。共通ヘッダに、デバイスごとの項目(extra)を続ける。
    static byte[] Function(ushort vendor, ushort device, byte baseClass, byte subClass, byte progIf, byte header, byte revision, params Writer[] extra) => Sequence(
        At(0x00, U16(vendor)),
        At(0x02, U16(device)),
        At(0x04, U16(0x0007)),              // コマンド: I/O 空間 + メモリ空間 + バスマスタを有効化済みにしておく
        At(0x06, U16(0x0200)),              // ステータス: DEVSEL=medium
        At(0x08, U8(revision)),
        At(0x09, U8(progIf)),
        At(0x0A, U8(subClass)),
        At(0x0B, U8(baseClass)),
        At(0x0E, U8(header)),               // ヘッダ種別(bit7 = マルチファンクション)
        Sequence(extra))
        .Build(256);

    // CONFIG_ADDRESS の分解: enable(bit31)、bus(23:16)、dev(15:11)、fn(10:8)、reg(7:2 = dword 境界のレジスタ番号)。
    (bool enabled, int bus, int dev, int fn, int reg) Decoded =>
        ((Address & 0x80000000) != 0, (int)(Address >> 16) & 0xFF, (int)(Address >> 11) & 0x1F, (int)(Address >> 8) & 0x7, (int)(Address & 0xFC));

    // 選択中のファンクションのコンフィグ空間(bus 0 で enable のとき。無ければ null)。off = ポート下位 2 ビット(0xCFC からのバイトずれ)。
    byte[] Selected(out int reg)
    {
        var (enabled, bus, dev, fn, register) = Decoded;
        reg = register;
        return enabled && bus == 0 && cfg.TryGetValue(Key(dev, fn), out var c) ? c : null;
    }

    // CONFIG_DATA 読み出し。存在しないデバイスと、コンフィグ空間(256 バイト)の外は 0xFF(ベンダ ID = 0xFFFF)。
    public uint DataRead(int off, int size)
    {
        var config = Selected(out var reg);
        uint value = 0;
        for (var index = 0; index < size; index++)
        {
            var position = reg + off + index;
            value |= (uint)(config != null && position < 256 ? config[position] : 0xFF) << (8 * index);
        }
        if (Log && config != null)
            Console.Error.WriteLine($"[pci] rd {Bdf()} reg {reg + off:x2} size {size} -> {value:x} @{Eip:x8} {Caller()}");
        return value;
    }

    int KeyOf() => Key(Decoded.dev, Decoded.fn);

    string Bdf() => $"{Decoded.bus:x2}:{Decoded.dev:x2}.{Decoded.fn}";

    // コンフィグ空間の 1 バイトのうち、書き換えられるビット。ベンダ/デバイス ID(0x00-0x03)、リビジョンとクラス(0x08-0x0B)、
    // ヘッダ種別(0x0E)は不変。BAR から拡張 ROM BAR まで(0x10-0x33)は、実装済みの BAR だけがマスク(barMask)のビットを受ける
    // (サイズ問い合わせに答えるため。未実装の BAR は 0 のまま読み取り専用)。それ以外(コマンドなど)は全ビット書ける。
    byte WritableBits(int key, int position) => position switch
    {
        < 0x04 or (>= 0x08 and <= 0x0B) or 0x0E => 0x00,
        >= 0x10 and < 0x34 => barMask.TryGetValue((key, position & ~3), out var mask) ? (byte)(mask >> (8 * (position & 3))) : (byte)0x00,
        _ => 0xFF,
    };

    // CONFIG_DATA 書き込み。各バイトは、書き換えられるビットだけが変わる。
    public void DataWrite(int off, int size, uint val)
    {
        var config = Selected(out var reg);
        if (config == null) return;
        if (Log) Console.Error.WriteLine($"[pci] wr {Bdf()} reg {reg + off:x2} size {size} <- {val:x} @{Eip:x8} {Caller()}");
        var key = KeyOf();
        for (var index = 0; index < size && reg + off + index < 256; index++)
        {
            var position = reg + off + index;
            var writable = WritableBits(key, position);
            config[position] = (byte)((config[position] & ~writable) | ((byte)(val >> (8 * index)) & writable));
        }
        if (key == VgaKey) UpdateLfbBase();
    }
}
