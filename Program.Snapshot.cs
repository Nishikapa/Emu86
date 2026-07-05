namespace Emu86;

static partial class Program
{
    // スナップショット(CPU + メモリ + I/O 状態)の保存先と自動保存間隔(命令数)。
    // 1命令ずつ解釈実行するため長時間のブートは避けられないので、
    // "--resume" で前回の続きから再開できるようにする。
    // "--snapshot <path>" で保存先を切り替えられる(ブート用チェックポイントと
    // 回帰テスト用を分離し、検証済み区間を再実行せずに済ませるため)。
    // 拡張子は .snap に統一する(.bin は BIOS/ディスクイメージ系と紛らわしいため)。
    static string SnapshotPath = "snapshot.snap";
    // 高速コア(約7M命令/秒)前提で、保存オーバーヘッドが走行時間の数%に収まる間隔にする。
    const long SnapshotInterval = 100_000_000;
    const string SnapshotMagic = "EMU86SNP";
    const int SnapshotVersion = 2; // v2: CPU に cr4 を追加

    static void SaveSnapshot(long count, CPU cpu, EmuEnvironment env)
    {
        // 書き込み中のプロセス強制終了で壊れたファイルが残らないよう、
        // 一時ファイルへ書いてから置き換える。
        var tmp = SnapshotPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes(SnapshotMagic));
            w.Write(SnapshotVersion);
            w.Write(count);
            cpu.WriteTo(w);
            env.SaveState(w);
        }
        File.Move(tmp, SnapshotPath, overwrite: true);
    }

    static (long count, CPU cpu) LoadSnapshot(EmuEnvironment env)
    {
        using var fs = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read);
        using var r = new BinaryReader(fs);
        if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(SnapshotMagic.Length)) != SnapshotMagic)
            throw new InvalidDataException($"{SnapshotPath} is not a valid Emu86 snapshot");
        if (r.ReadInt32() != SnapshotVersion)
            throw new InvalidDataException($"{SnapshotPath} has an unsupported snapshot version");
        var count = r.ReadInt64();
        var cpu = CPU.ReadFrom(r);
        env.LoadState(r);
        return (count, cpu);
    }
}
