using static Emu86.Ext;

namespace Emu86;

// 翻訳キャッシュ(docs/adr/0002 段階 2)。命令の先頭物理アドレス(とコードセグメントの D ビット)をキーに、
// デコード済み命令の IR を直結マップで持つ。段階は 3 つ:
//   1. TranslateAfter 回実行されたらデコードして IR に下ろし、以後は IR 解釈器で実行する(デコード省略。下ろすのは安い)。
//   2. CompileAfter 回実行されたら Expression.Compile でデリゲートにする(1 サイトあたり約 1ms かかるので、熱いサイトだけ)。
//   IR の対象外は「翻訳不可」と記録して再デコードを避ける。
// 翻訳した命令が載る物理ページには印を付け、そのページへの書き込みで該当エントリを無効化する(自己書き換えコード)。
// RAM への書き込みはすべて Environment の書き込み経路を通る前提(直接書くところは InvalidateAll を呼ぶ)。
// ページ境界をまたぐ命令は、後半のページが差し替えられても検出できないので翻訳しない。
public sealed class TranslationCache
{
    public const int TranslateAfter = 8;
    public static int CompileAfter = 4096; // --compile-after で変えられる(大きくすると解釈のみに近づく)
    const int Size = 1 << 16;

    // 16 バイト(表は 64K 項で 1MB)。毎命令ランダムに引くので小さく保つ。
    struct Entry
    {
        public uint Tag;               // 物理アドレス
        public int Count;              // 実行回数。0 = 空。bit31 = 翻訳不可、bit30 = 32 ビットコード
        public object Code;            // Block(段階 1: 解釈実行)または Action<EmuEnvironment, CPU>(段階 2: コンパイル済み)

        public const int Untranslatable = int.MinValue;
        public const int Code32Flag = 1 << 30;
        public const int CountMask = Code32Flag - 1;
    }

    readonly Entry[] entries = new Entry[Size];
    readonly byte[] codePages;                                   // 物理ページごとの「翻訳済み命令あり」
    readonly Dictionary<uint, List<int>> pageEntries = new();    // ページ → そのページの命令を持つエントリ番号

    // 既定は無効(--translate で有効)。1 命令単位の翻訳は高速コアと同等以下だった(docs/adr/0002 の計測)。
    public bool Enabled;
    public long Translated;   // IR に下ろした命令サイトの数
    public long Compiled;     // コンパイルした命令サイトの数
    public long Interpreted;  // IR 解釈器で実行した命令数
    public long Executed;     // コンパイル済みデリゲートで実行した命令数
    public long Invalidations;
    public double CompileMilliseconds; // Expression.Compile に使った時間

    public TranslationCache(int ramSize) => codePages = new byte[(ramSize + 0xFFF) >> 12];

    // 翻訳済みなら実行して true。未翻訳・対象外なら false(呼び出し側が高速コアへ)。
    // 物理アドレスへの変換はフェッチと同じ経路なので、不在ページなら同じ #PF が出る。
    public bool TryRun(EmuEnvironment env, CPU cpu)
    {
        var linear = GetCodeAddr(cpu).addr;
        var physical = env.PagingOn ? EnvTranslate(env, linear, false) : EnvAlias(linear);
        if (physical >= (uint)codePages.Length << 12) return false;
        var index = (int)(physical & (Size - 1));
        ref var entry = ref entries[index];
        var code32 = cpu.code32 ? Entry.Code32Flag : 0;
        if (entry.Count == 0 || entry.Tag != physical || (entry.Count & Entry.Code32Flag) != code32)
        {
            entry = new Entry { Tag = physical, Count = 1 | code32 };
            return false;
        }
        if (entry.Code is Action<EmuEnvironment, CPU> run)
        {
            Executed++;
            run(env, cpu);
            return true;
        }
        if (entry.Count < 0) return false; // 翻訳不可
        entry.Count++;
        if (entry.Code is Block ir)
        {
            if ((entry.Count & Entry.CountMask) >= CompileAfter)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var compiled = Semantics.Compile(ir);
                CompileMilliseconds += watch.Elapsed.TotalMilliseconds;
                entry.Code = compiled;
                Compiled++;
                Executed++;
                compiled(env, cpu);
                return true;
            }
            Interpreted++;
            ir.Run(env, cpu);
            return true;
        }
        if ((entry.Count & Entry.CountMask) < TranslateAfter) return false;

        var instruction = DecodeInstruction(env, cpu);
        var crossesPage = ((physical + (uint)instruction.Length - 1) >> 12) != (physical >> 12);
        var block = crossesPage ? null : Semantics.Lower(instruction);
        if (block == null)
        {
            entry.Count |= Entry.Untranslatable;
            return false;
        }
        entry.Code = block;
        Translated++;
        var page = physical >> 12;
        codePages[page] = 1;
        if (!pageEntries.TryGetValue(page, out var list)) pageEntries[page] = list = [];
        list.Add(index);
        Interpreted++;
        block.Run(env, cpu);
        return true;
    }

    // 物理アドレスへの書き込み: そのページに翻訳済み命令があれば無効化する(書き込み経路から毎回呼ばれるので、無ければ即帰る)。
    public void OnWrite(uint physical)
    {
        var page = physical >> 12;
        if (page >= (uint)codePages.Length || codePages[page] == 0) return;
        codePages[page] = 0;
        if (pageEntries.Remove(page, out var list))
            foreach (var index in list) entries[index] = default;
        Invalidations++;
    }

    // RAM が丸ごと差し替わったとき(スナップショット復元、ROM の直接書き込み)。
    public void InvalidateAll()
    {
        Array.Clear(entries);
        Array.Clear(codePages);
        pageEntries.Clear();
    }
}
