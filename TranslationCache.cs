using System.Linq.Expressions;
using static Emu86.Conversions;
using static Emu86.Mmu;
using static Emu86.Ports;
using static Emu86.Decoder;

namespace Emu86;

// コンパイル済みブロック: 予算(実行してよい命令数)と開始時の世代を受け取り、完了した命令数を返す。
public delegate int BlockRun(EmuEnvironment env, CPU cpu, int budget, long generation);

// 基本ブロック(docs/adr/0002 段階 3、連結は段階 6): 先頭から分岐(Jcc/JMP/CALL/RET)か STI まで、または IR 対象外の命令の手前・ページ末尾・上限までの
// IR 命令列。実行は 1 命令ずつ(各命令の IR は独立に完結し、EIP は命令末尾で進む)で、
//   予算: 次のイベント(割り込み周期・サービス周期・スナップショット・上限)までの命令数を超えない
//   世代: ブロック内の命令が翻訳済みページへ書いたら(自己書き換え)、そこで止める
//   フォルト: IR の規律(書き込み先の事前検査、PUSH の順序)により、投げられた時点の CPU は失敗した命令の先頭の状態
// という性質を保つので、割り込みの配送点・トレース・スナップショットは 1 命令ずつの実行と同じ境界に揃う。
public sealed class TranslatedBlock(Block[] instructions, uint[] offsets, bool stopsChain, bool usesClock, bool final)
{
    public readonly Block[] Instructions = instructions;
    public readonly uint[] Offsets = offsets;   // 各命令のブロック先頭からのオフセット
    // 最後の命令が env.Tsc を読む(RDTSC、ポート入出力の時計: PIT/RTC/PM タイマ)。ランナーは Step 開始時の命令数を Tsc に置くだけなので、
    // 実行前に「Step 開始時 + 完了済み + ブロック内の位置」を置いて 1 命令ずつの実行と同じ値にする。
    public readonly bool UsesClock = usesClock;
    public readonly bool StopsChain = stopsChain; // 連結しない(STI/POPF: 保留中の割り込みを直後の Step 境界で配送する。HLT: ランナーが待機を見る。IN/OUT 系: デバイスが割り込みを立てうる)
    // 1 命令のブロックは最初の実行で作り、BlockAfter 回に達したら先頭から複数命令のブロックに下ろし直す。Final はその必要が無い
    // (すでに複数命令で下ろした、1 命令ずつの設定、ページをまたぐ命令)。
    public bool Final = final;
    public BlockRun Compiled;

    public int Interpret(EmuEnvironment env, CPU cpu, int budget, long generation)
    {
        var executed = 0;
        foreach (var instruction in Instructions)
        {
            if (executed == budget) break;
            instruction.Run(env, cpu);
            executed++;
            if (env.Translations.Generation != generation) break;
        }
        return executed;
    }

    // 命令ごとに「予算 → 命令 → 完了数の更新 → 世代検査」を並べた 1 つのラムダにする。
    public BlockRun Compile()
    {
        var env = Expression.Parameter(typeof(EmuEnvironment), "env");
        var cpu = Expression.Parameter(typeof(CPU), "cpu");
        var budget = Expression.Parameter(typeof(int), "budget");
        var generation = Expression.Parameter(typeof(long), "generation");
        var executed = Expression.Variable(typeof(int), "executed");
        var exit = Expression.Label(typeof(int), "exit");
        var currentGeneration = Expression.Field(Expression.Field(env, nameof(EmuEnvironment.Translations)), nameof(TranslationCache.Generation));
        var body = new List<Expression> { Expression.Assign(executed, Expression.Constant(0)) };
        for (var index = 0; index < Instructions.Length; index++)
        {
            body.Add(Expression.IfThen(Expression.Equal(executed, budget), Expression.Return(exit, executed)));
            body.Add(Semantics.CompileInto(Instructions[index], env, cpu));
            body.Add(Expression.Assign(executed, Expression.Constant(index + 1)));
            body.Add(Expression.IfThen(Expression.NotEqual(currentGeneration, generation), Expression.Return(exit, executed)));
        }
        body.Add(Expression.Label(exit, executed));
        return Expression.Lambda<BlockRun>(Expression.Block([executed], body), env, cpu, budget, generation).Compile();
    }
}

// 翻訳キャッシュ。ブロックの先頭物理アドレス(とコードセグメントの D ビット)をキーにした直結表(256K 項、1 項 16 バイト)。
//   1. 最初の実行で、その 1 命令だけを IR に下ろして解釈実行する(docs/adr/0003 段階 3: 高速コアの代わり)
//   2. BlockAfter 回実行されたら先頭から複数命令のブロックに下ろし直す(BlockLimit = 1 の「1 命令ずつ」の設定では行わない)
//   3. CompileAfter 回実行されたら Expression.Compile でデリゲートにする(1 ブロックあたり 1ms 前後)
//   IR の対象外で始まる位置は「翻訳不可」と記録して再デコードを避ける(通常コアへ)。
// 翻訳したブロックが載る物理ページには印を付け、そのページへの書き込みで該当エントリを無効化し世代を進める。
// RAM への書き込みはすべて Environment の書き込み経路を通る前提(直接書くところは InvalidateAll を呼ぶ)。
// 複数命令のブロックは 1 つの物理ページに収める。ページをまたぐ命令は 1 命令のブロックとして両方のページに登録する。
public sealed class TranslationCache
{
    public static int CompileAfter = 4096; // --compile-after で変えられる(大きくすると解釈のみに近づく)
    public int SingleAfter = 2;            // 1 命令のブロックを作るまでの実行回数(1 度しか実行しない命令は通常コアに任せて、下ろす費用を省く)
    public int BlockAfter = 8;             // 1 命令のブロックを複数命令に下ろし直すまでの実行回数
    public int BlockLimit = 64;            // 1 ブロックの最大命令数(1 = 1 命令ずつ: --no-translate と命令ごとの診断)
    const int Size = 1 << 18;

    struct Entry
    {
        public uint Tag;               // 物理アドレス
        public int Count;              // 実行回数。0 = 空。bit31 = 翻訳不可、bit30 = 32 ビットコード
        public TranslatedBlock Block;

        public const int Untranslatable = int.MinValue;
        public const int Code32Flag = 1 << 30;
        public const int CountMask = Code32Flag - 1;
    }

    // 2 ウェイのセットアソシアティブ(1 セット = 連続する 2 項)。空きが無ければ実行回数の少ない方を追い出す(冷たい 1 命令のブロックが
    // 熱いブロックを押し出さないため)。
    readonly Entry[] entries = new Entry[Size * 2];
    readonly byte[] codePages;                                   // 物理ページごとの「翻訳済みブロックあり」
    readonly Dictionary<uint, List<int>> pageEntries = new();    // ページ → そのページのブロックを持つエントリ番号

    // 既定は無効(--translate で有効)。
    public bool Enabled;
    // ブロックの連結(テストが 1 ブロックずつ見るために切れる。ランナーは常に連結する)。
    public bool Chain = true;
    public long Generation;   // 無効化のたびに進む(実行中のブロックが自己書き換えを検出する)
    public readonly uint[] Temps = new uint[8]; // IR 解釈器の Let(一時変数)の置き場
    public readonly CPU Checkpoint = new();     // 通常コアの State を呼ぶ文(StateStmt)がフォルト時に戻すための退避先
    public long Translated;   // IR に下ろした回数(1 命令のブロックを含む)
    public long Blocks;       // 複数命令のブロックに下ろした回数
    public long Compiled;     // コンパイルしたブロック数
    public long Runs;         // ブロックを実行した回数(平均ブロック長 = 命令数 / Runs)
    public long Steps;        // TryRun が true を返した回数(連結したブロックの列 = ランナーの 1 Step)
    // 連結の途中の制御転送(直前の命令の先頭 EIP、分岐先 EIP)。トレースが有効なときだけランナーが用意し、トレース行にする。
    public List<(uint From, uint To)> Transfers;
    public long Interpreted;  // IR 解釈器で実行した命令数
    public long Executed;     // コンパイル済みデリゲートで実行した命令数
    public long Invalidations;
    public double CompileMilliseconds; // Expression.Compile に使った時間
    public double LowerMilliseconds;   // デコードと IR への下ろしに使った時間

    // フォルトで中断したブロックの進み(実行器が読む): 完了した命令数と、その最後の命令の先頭 EIP。
    public int FaultExecuted;
    public uint FaultLastEip;

    public TranslationCache(int ramSize) => codePages = new byte[(ramSize + 0xFFF) >> 12];

    // 翻訳済みブロックがあれば予算の範囲で実行して true(executed = 完了した命令数、lastEip = 最後の命令の先頭)。
    // 未翻訳・対象外なら false(呼び出し側が高速コアへ)。先頭の物理アドレスへの変換はフェッチと同じ経路なので、
    // 不在ページなら同じ #PF が出る。
    // ブロックの連結(docs/adr/0002 段階 6): ブロックが分岐で終わり、予算が残り、分岐先も翻訳済みなら、ランナーへ戻らずに
    // 続けて実行する。止まるのは、予算・世代の変化(自己書き換え)・STI(保留中の割り込みの配送点)・翻訳済みでない分岐先。
    // ブロックの途中で新たに配送できる割り込みが生じる契機は STI と、IR の対象外の命令(POPF/IRET/OUT)だけなので、
    // 連結しても配送点は 1 命令ずつの実行と同じ Step 境界になる。
    public bool TryRun(EmuEnvironment env, CPU cpu, int budget, out int executed, out uint lastEip)
    {
        executed = 0;
        lastEip = cpu.eip;
        Transfers?.Clear();
        var clock = env.Tsc; // Step 開始時の命令数
        while (true)
        {
            // 先頭のフェッチ(キーの変換)やデコードでの #PF は、連結済みの分だけ完了している。
            FaultExecuted = executed;
            FaultLastEip = lastEip;
            var block = Find(env, cpu);
            if (block == null) return executed > 0;
            var startEip = cpu.eip;
            var generation = Generation;
            Runs++;
            if (block.UsesClock) env.Tsc = clock + (ulong)(executed + block.Instructions.Length - 1);
            int completed;
            try
            {
                if (block.Compiled != null)
                {
                    completed = block.Compiled(env, cpu, budget - executed, generation);
                    Executed += completed;
                }
                else
                {
                    completed = block.Interpret(env, cpu, budget - executed, generation);
                    Interpreted += completed;
                }
            }
            catch (CpuFaultException)
            {
                // IR の規律により CPU は失敗した命令の先頭の状態。EIP からその命令の番号を求め、完了数として報告する。
                var failed = Array.IndexOf(block.Offsets, cpu.eip - startEip);
                if (failed < 0) failed = 0;
                FaultExecuted = executed + failed;
                FaultLastEip = failed > 0 ? startEip + block.Offsets[failed - 1] : lastEip;
                throw;
            }
            executed += completed;
            lastEip = startEip + block.Offsets[completed - 1];
            if (!Chain || executed >= budget || Generation != generation || completed < block.Instructions.Length || block.StopsChain)
            {
                Steps++;
                return true;
            }
            Transfers?.Add((lastEip, cpu.eip));
        }
    }

    // 現在の CS:EIP のブロックを返す(最初の実行で 1 命令を下ろし、実行回数の段階に応じて複数命令に下ろし直し、コンパイルする)。
    // IR の対象外の命令なら null(通常コアへ)。
    TranslatedBlock Find(EmuEnvironment env, CPU cpu)
    {
        var linear = GetCodeAddr(cpu).addr;
        var physical = env.Paging.Enabled ? EnvTranslate(env, linear, false) : EnvAlias(linear);
        if (physical >= (uint)codePages.Length << 12) return null;
        var set = (int)(physical & (Size - 1)) * 2;
        var code32 = cpu.code32 ? Entry.Code32Flag : 0;
        var index = set;
        if (!Matches(entries[set], physical, code32))
        {
            if (Matches(entries[set + 1], physical, code32)) index = set + 1;
            else index = entries[set].Count == 0 ? set : entries[set + 1].Count == 0 ? set + 1
                : (entries[set].Count & Entry.CountMask) <= (entries[set + 1].Count & Entry.CountMask) ? set : set + 1;
        }
        ref var entry = ref entries[index];
        if (!Matches(entry, physical, code32))
        {
            entry = new Entry { Tag = physical, Count = 1 | code32 };
            if (SingleAfter > 1) return null;
        }
        else if (entry.Count < 0) return null; // 翻訳不可
        else entry.Count++;
        var count = entry.Count & Entry.CountMask;
        var block = entry.Block;
        if (block == null)
        {
            if (count < SingleAfter) return null;
            block = Lower(env, cpu, physical, 1, out var secondPage);
            if (block == null)
            {
                entry.Count |= Entry.Untranslatable;
                return null;
            }
            entry.Block = block;
            Translated++;
            Register(index, physical >> 12);
            if (secondPage is uint page) Register(index, page);
            return block;
        }
        if (!block.Final && count >= BlockAfter)
        {
            var expanded = Lower(env, cpu, physical, BlockLimit, out _);
            if (expanded != null) { block = entry.Block = expanded; Translated++; Blocks++; }
            else block.Final = true;
        }
        else if (block.Compiled == null && count >= CompileAfter)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            block.Compiled = block.Compile();
            CompileMilliseconds += watch.Elapsed.TotalMilliseconds;
            Compiled++;
        }
        return block;
    }

    static bool Matches(in Entry entry, uint physical, int code32) =>
        entry.Count != 0 && entry.Tag == physical && (entry.Count & Entry.Code32Flag) == code32;

    void Register(int index, uint page)
    {
        codePages[page] = 1;
        if (!pageEntries.TryGetValue(page, out var list)) pageEntries[page] = list = [];
        list.Add(index);
    }

    // ブロックを先頭からデコードして IR に下ろす(limit 命令まで)。対象外の命令で始まるなら null。
    // 分岐(または STI など)を含めたらそこで終わる。2 命令目以降はページ末尾の 15 バイトに掛かる前に止める(デコードが隣のページを読まないため)。
    // 先頭の命令がページをまたぐときは 1 命令だけにして、2 つ目のページ(secondPage)も登録する(どちらへの書き込みでも無効化される)。
    TranslatedBlock Lower(EmuEnvironment env, CPU cpu, uint physical, int limit, out uint? secondPage)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return LowerCore(env, cpu, physical, limit, out secondPage); }
        finally { LowerMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    TranslatedBlock LowerCore(EmuEnvironment env, CPU cpu, uint physical, int limit, out uint? secondPage)
    {
        secondPage = null;
        var instructions = new List<Block>();
        var offsets = new List<uint>();
        var offset = 0u;
        var pageRemaining = 0x1000 - (physical & 0xFFF);
        var stopsChain = false;
        var usesClock = false;
        var final = limit > 1 || BlockLimit == 1;
        while (instructions.Count < limit && (offset == 0 || offset + 15 <= pageRemaining))
        {
            var code = new CodeSource(env, cpu.cs_base, cpu.eip + offset, cpu.code32);
            var (_, instruction, length) = Decode(code, cpu.eip + offset, cpu.code32);
            instruction = instruction with { Length = length };
            var lowered = Semantics.Lower(instruction);
            if (lowered == null) break;
            var crosses = offset + (uint)length > pageRemaining;
            if (crosses && instructions.Count > 0) break;
            instructions.Add(lowered);
            offsets.Add(offset);
            offset += (uint)length;
            if (crosses)
            {
                var last = code.Address(length - 1);
                var physicalLast = env.Paging.Enabled ? EnvTranslate(env, last, false) : EnvAlias(last);
                if (physicalLast < (uint)codePages.Length << 12) secondPage = physicalLast >> 12;
                final = true;
                break;
            }
            if (Semantics.EndsBlock(instruction)) { stopsChain = Semantics.StopsChain(instruction); usesClock = Semantics.UsesClock(instruction); break; }
        }
        return instructions.Count == 0 ? null : new TranslatedBlock([.. instructions], [.. offsets], stopsChain, usesClock, final);
    }

    // 物理アドレスへの書き込み: そのページに翻訳済みブロックがあれば無効化する(書き込み経路から毎回呼ばれるので、無ければ即帰る)。
    public void OnWrite(uint physical)
    {
        var page = physical >> 12;
        if (page >= (uint)codePages.Length || codePages[page] == 0) return;
        codePages[page] = 0;
        if (pageEntries.Remove(page, out var list))
            foreach (var index in list) entries[index] = default;
        Invalidations++;
        Generation++;
    }

    // RAM が丸ごと差し替わったとき(スナップショット復元、ROM の直接書き込み)。
    public void InvalidateAll()
    {
        Array.Clear(entries);
        Array.Clear(codePages);
        pageEntries.Clear();
        Generation++;
    }
}
