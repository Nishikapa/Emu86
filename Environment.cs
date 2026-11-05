namespace Emu86;

// エミュレータの環境(CPU の外にある状態): 物理メモリ、I/O ポート、デバイス(Environment.Devices.cs)、ページングの状態と TLB、
// 翻訳キャッシュ、診断、時計(TSC)、MSR、DR。保存形式は Environment.Snapshot.cs。
public partial class EmuEnvironment : IDisposable
{
    // 搭載RAM量。SeaBIOS の init 再配置は 1MB 超のRAMを要求するため、
    // 1MBちょうどではなく拡張メモリを持たせる。BIOS(bios.bin)は先頭1MBの末尾に配置される。
    public const int RamSize = 256 * 1024 * 1024; // 256MB(Debian の initramfs をロードするには 32MB では不足)

    public EmuEnvironment(byte[] memory = null, byte[] bios = null, AtaDevice ata = null)
    {
        Ram = memory ?? new byte[RamSize];
        if (bios is { Length: > 0 })
        {
            if (bios.Length > 0x100000 || Ram.Length < 0x100000)
                throw new ArgumentException("BIOS must fit in the first megabyte of RAM", nameof(bios));
            bios.CopyTo(Ram, 0x100000 - bios.Length);
        }
        CmosRtc = new CmosDevice(Ram.Length, Clock);
        Pit = new PitDevice(Clock);
        Acpi = new AcpiPmDevice(Clock);
        Translations = new TranslationCache(Ram.Length);
        Ata = ata;
    }

    public void Dispose()
    {
        Ata?.Dispose();
        Com1.Host?.Dispose(); Com1.Host = null;
        Com2.Host?.Dispose(); Com2.Host = null;
    }

    // プライマリ ATA チャネルのマスタドライブ(未接続なら null)。
    public AtaDevice Ata;

    // PCI ホスト(コンフィグ機構 #1)。ata_piix を bind させるため PIIX3 IDE を露出する。
    public PciHost Pci = new();

    // ゲストの時計(1 秒 = N 命令。docs/adr/0004)。スナップショットに保存する。
    public readonly GuestClock Clock = new();

    // 物理メモリ(RamSize バイト。BIOS は先頭 1MB の末尾に置く)。VGA ウィンドウや ROM 窓はこの外で扱う。
    public byte[] Ram;

    public byte[] IoPort = new byte[0x10000];

    // 翻訳キャッシュ(docs/adr/0002)。RAM への書き込みで該当ページの翻訳を捨てる。スナップショットには含めない。
    public TranslationCache Translations;

    // ページング変換状態と TLB(CPU の制御レジスタのミラー。Paging.Sync で同期。スナップショットには含めない)。
    public readonly Paging Paging = new();

    // ウォッチポイント・書き込みログ・割り込みログ(デバッグ用)。何も有効でなければ null。
    public MemoryDiagnostics Diagnostics;

    // タイムスタンプカウンタの代用(Runner が毎命令、実行済み命令数を書き込む)。
    public ulong Tsc;

    // システムリセット要求(ポート 0xCF9 または 8042 経由)。ランナーが見て実行を抜ける。スナップショットには含めない。
    public bool ResetRequested;

    // MSR の汎用ストア(RDMSR/WRMSR)。未書き込みは 0 として読める。
    public Dictionary<uint, ulong> Msrs = new();

    // デバッグレジスタ DR0-DR7(保持のみ。ブレークポイント機能はなし)。
    public uint[] Dr = new uint[8];
}
