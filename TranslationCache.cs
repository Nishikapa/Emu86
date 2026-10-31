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
public sealed class TranslatedBlock(Block[] instructions, uint[] offsets, bool stopsChain, bool usesClock)
{
    public readonly Block[] Instructions = instructions;
    public readonly uint[] Offsets = offsets;   // 各命令のブロック先頭からのオフセット
    // 最後の命令が env.Tsc を読む(RDTSC、ポート入出力の時計: PIT/RTC/PM タイマ)。ランナーは Step 開始時の命令数を Tsc に置くだけなので、
    // 実行前に「Step 開始時 + 完了済み + ブロック内の位置」を置いて 1 命令ずつの実行と同じ値にする。
    public readonly bool UsesClock = usesClock;
    public readonly bool StopsChain = stopsChain; // 連結しない(STI/POPF: 保留中の割り込みを直後の Step 境界で配送する。HLT: ランナーが待機を見る。IN/OUT 系: デバイスが割り込みを立てうる)
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

// 翻訳キャッシュ。ブロックの先頭物理アドレス(とコードセグメントの D ビット)をキーにした直結表(64K 項、1 項 16 バイト)。
//   1. TranslateAfter 回実行されたら先頭からブロックをデコードして IR に下ろし、以後は解釈実行する
//   2. CompileAfter 回実行されたら Expression.Compile でデリゲートにする(1 ブロックあたり 1ms 前後)
//   IR の対象外で始まる位置は「翻訳不可」と記録して再デコードを避ける。
// 翻訳したブロックが載る物理ページには印を付け、そのページへの書き込みで該当エントリを無効化し世代を進める。
// RAM への書き込みはすべて Environment の書き込み経路を通る前提(直接書くところは InvalidateAll を呼ぶ)。
// ブロックは 1 つの物理ページに収める(後半のページの差し替えを検出できないため)。
public sealed class TranslationCache
{
    public const int TranslateAfter = 8;
    public static int CompileAfter = 4096; // --compile-after で変えられる(大きくすると解釈のみに近づく)
    public const int MaxBlock = 64;         // 1 ブロックの最大命令数
    const int Size = 1 << 16;

    struct Entry
    {
        public uint Tag;               // 物理アドレス
        public int Count;              // 実行回数。0 = 空。bit31 = 翻訳不可、bit30 = 32 ビットコード
        public TranslatedBlock Block;

        public const int Untranslatable = int.MinValue;
        public const int Code32Flag = 1 << 30;
        public const int CountMask = Code32Flag - 1;
    }

    readonly Entry[] entries = new Entry[Size];
    readonly byte[] codePages;                                   // 物理ページごとの「翻訳済みブロックあり」
    readonly Dictionary<uint, List<int>> pageEntries = new();    // ページ → そのページのブロックを持つエントリ番号

    // 既定は無効(--translate で有効)。
    public bool Enabled;
    // ブロックの連結(テストが 1 ブロックずつ見るために切れる。ランナーは常に連結する)。
    public bool Chain = true;
    public long Generation;   // 無効化のたびに進む(実行中のブロックが自己書き換えを検出する)
    public readonly uint[] Temps = new uint[8]; // IR 解釈器の Let(一時変数)の置き場
    public readonly CPU Checkpoint = new();     // 通常コアの State を呼ぶ文(StateStmt)がフォルト時に戻すための退避先
    public long Translated;   // IR に下ろしたブロック数
    public long Compiled;     // コンパイルしたブロック数
    public long Runs;         // ブロックを実行した回数(平均ブロック長 = 命令数 / Runs)
    public long Steps;        // TryRun が true を返した回数(連結したブロックの列 = ランナーの 1 Step)
    // 連結の途中の制御転送(直前の命令の先頭 EIP、分岐先 EIP)。トレースが有効なときだけランナーが用意し、トレース行にする。
    public List<(uint From, uint To)> Transfers;
    public long Interpreted;  // IR 解釈器で実行した命令数
    public long Executed;     // コンパイル済みデリゲートで実行した命令数
    public long Invalidations;
    public double CompileMilliseconds; // Expression.Compile に使った時間

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

    // 現在の CS:EIP のブロックを返す(実行回数の段階に応じて IR に下ろし、コンパイルする)。翻訳済みでなければ null。
    TranslatedBlock Find(EmuEnvironment env, CPU cpu)
    {
        var linear = GetCodeAddr(cpu).addr;
        var physical = env.Paging.Enabled ? EnvTranslate(env, linear, false) : EnvAlias(linear);
        if (physical >= (uint)codePages.Length << 12) return null;
        var index = (int)(physical & (Size - 1));
        ref var entry = ref entries[index];
        var code32 = cpu.code32 ? Entry.Code32Flag : 0;
        if (entry.Count == 0 || entry.Tag != physical || (entry.Count & Entry.Code32Flag) != code32)
        {
            entry = new Entry { Tag = physical, Count = 1 | code32 };
            return null;
        }
        if (entry.Count < 0) return null; // 翻訳不可
        entry.Count++;
        var block = entry.Block;
        if (block == null)
        {
            if ((entry.Count & Entry.CountMask) < TranslateAfter) return null;
            block = Lower(env, cpu, physical);
            if (block == null)
            {
                entry.Count |= Entry.Untranslatable;
                return null;
            }
            entry.Block = block;
            Translated++;
            var page = physical >> 12;
            codePages[page] = 1;
            if (!pageEntries.TryGetValue(page, out var list)) pageEntries[page] = list = [];
            list.Add(index);
        }
        else if (block.Compiled == null && (entry.Count & Entry.CountMask) >= CompileAfter)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            block.Compiled = block.Compile();
            CompileMilliseconds += watch.Elapsed.TotalMilliseconds;
            Compiled++;
        }
        return block;
    }

    // ブロックを先頭からデコードして IR に下ろす。対象外の命令で始まるなら null。
    // 分岐(または STI)を含めたらそこで終わる。命令がページ末尾の 15 バイトに掛かる前に止める(デコードが隣のページを読まないため)。
    static TranslatedBlock Lower(EmuEnvironment env, CPU cpu, uint physical)
    {
        var instructions = new List<Block>();
        var offsets = new List<uint>();
        var offset = 0u;
        var pageRemaining = 0x1000 - (physical & 0xFFF);
        var stopsChain = false;
        var usesClock = false;
        while (instructions.Count < MaxBlock && offset + 15 <= pageRemaining)
        {
            var code = new CodeSource(env, cpu.cs_base, cpu.eip + offset, cpu.code32);
            var (_, instruction, length) = InstructionParser(cpu.eip + offset, cpu.code32)(code, 0);
            instruction = instruction with { Length = length };
            var lowered = Semantics.Lower(instruction);
            if (lowered == null) break;
            instructions.Add(lowered);
            offsets.Add(offset);
            offset += (uint)length;
            if (Semantics.EndsBlock(instruction)) { stopsChain = Semantics.StopsChain(instruction); usesClock = Semantics.UsesClock(instruction); break; }
        }
        return instructions.Count == 0 ? null : new TranslatedBlock([.. instructions], [.. offsets], stopsChain, usesClock);
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
