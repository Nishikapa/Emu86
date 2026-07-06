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
    // v2: CPU に cr4 + FPU 制御/状態ワードを追加
    // v3: 末尾に TSC と MSR ストアを追加
    // v4: 末尾に PIC 状態(ベース/マスク)を追加
    // v5: 末尾に x87 レジスタスタック(top/valid/ST0-7)を追加
    // (旧バージョンは互換ロード可能: 足りない分は既定値で補われる)
    const int SnapshotVersion = 5;

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
            // v3: TSC + MSR ストア
            w.Write(env.Tsc);
            w.Write(env.Msrs.Count);
            foreach (var (k, v) in env.Msrs)
            {
                w.Write(k);
                w.Write(v);
            }
            // v4: PIC 状態
            w.Write(env.PicMasterBase); w.Write(env.PicSlaveBase);
            w.Write(env.PicMasterMask); w.Write(env.PicSlaveMask);
            // v5: x87 レジスタスタック
            w.Write(cpu.fpu_top);
            w.Write(cpu.fpu_valid);
            for (var i = 0; i < 8; i++)
                w.Write(cpu.fpu_st[i]);
        }
        File.Move(tmp, SnapshotPath, overwrite: true);
    }

    static (long count, CPU cpu) LoadSnapshot(EmuEnvironment env)
    {
        using var fs = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read);
        using var r = new BinaryReader(fs);
        if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(SnapshotMagic.Length)) != SnapshotMagic)
            throw new InvalidDataException($"{SnapshotPath} is not a valid Emu86 snapshot");
        var version = r.ReadInt32();
        if (version is not (>= 2 and <= 5))
            throw new InvalidDataException($"{SnapshotPath} has an unsupported snapshot version");
        var count = r.ReadInt64();
        var cpu = CPU.ReadFrom(r);
        env.LoadState(r);
        if (version >= 3)
        {
            env.Tsc = r.ReadUInt64();
            var n = r.ReadInt32();
            for (var i = 0; i < n; i++)
            {
                var k = r.ReadUInt32();
                env.Msrs[k] = r.ReadUInt64();
            }
        }
        else
        {
            env.Tsc = (ulong)count; // v2 には無いので命令数で代用
        }
        if (version >= 4)
        {
            env.PicMasterBase = r.ReadByte(); env.PicSlaveBase = r.ReadByte();
            env.PicMasterMask = r.ReadByte(); env.PicSlaveMask = r.ReadByte();
        }
        if (version >= 5)
        {
            cpu.fpu_top = r.ReadInt32();
            cpu.fpu_valid = r.ReadByte();
            for (var i = 0; i < 8; i++)
                cpu.fpu_st[i] = r.ReadDouble();
        }
        return (count, cpu);
    }
}
