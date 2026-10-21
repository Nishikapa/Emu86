using System.Linq.Expressions;
using static Emu86.Ext;

namespace Emu86;

// コンパイル済みブロック: 予算(実行してよい命令数)と開始時の世代を受け取り、完了した命令数を返す。
public delegate int BlockRun(EmuEnvironment env, CPU cpu, int budget, long generation);

// 基本ブロック(docs/adr/0002 段階 3): 先頭から分岐(Jcc/JMP)まで、または IR 対象外の命令の手前・ページ末尾・上限までの
// IR 命令列。実行は 1 命令ずつ(各命令の IR は独立に完結し、EIP は命令末尾で進む)で、
//   予算: 次のイベント(割り込み周期・サービス周期・スナップショット・上限)までの命令数を超えない
//   世代: ブロック内の命令が翻訳済みページへ書いたら(自己書き換え)、そこで止める
//   フォルト: IR の規律(書き込み先の事前検査、PUSH の順序)により、投げられた時点の CPU は失敗した命令の先頭の状態
// という性質を保つので、割り込みの配送点・トレース・スナップショットは 1 命令ずつの実行と同じ境界に揃う。
public sealed class TranslatedBlock(Block[] instructions, uint[] offsets)
{
    public readonly Block[] Instructions = instructions;
    public readonly uint[] Offsets = offsets;   // 各命令のブロック先頭からのオフセット
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
    public long Generation;   // 無効化のたびに進む(実行中のブロックが自己書き換えを検出する)
    public long Translated;   // IR に下ろしたブロック数
    public long Compiled;     // コンパイルしたブロック数
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
    public bool TryRun(EmuEnvironment env, CPU cpu, int budget, out int executed, out uint lastEip)
    {
        executed = 0;
        lastEip = cpu.eip;
        // 先頭のフェッチ(キーの変換)やデコードでの #PF は何も完了していない。
        FaultExecuted = 0;
        FaultLastEip = cpu.eip;
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
        if (entry.Count < 0) return false; // 翻訳不可
        entry.Count++;
        var block = entry.Block;
        if (block == null)
        {
            if ((entry.Count & Entry.CountMask) < TranslateAfter) return false;
            block = Lower(env, cpu, physical);
            if (block == null)
            {
                entry.Count |= Entry.Untranslatable;
                return false;
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
        var startEip = cpu.eip;
        var generation = Generation;
        try
        {
            if (block.Compiled != null)
            {
                executed = block.Compiled(env, cpu, budget, generation);
                Executed += executed;
            }
            else
            {
                executed = block.Interpret(env, cpu, budget, generation);
                Interpreted += executed;
            }
        }
        catch (CpuFaultException)
        {
            // IR の規律により CPU は失敗した命令の先頭の状態。EIP からその命令の番号を求め、完了数として報告する。
            var offset = cpu.eip - startEip;
            var failed = Array.IndexOf(block.Offsets, offset);
            FaultExecuted = failed < 0 ? 0 : failed;
            FaultLastEip = failed > 0 ? startEip + block.Offsets[failed - 1] : startEip;
            throw;
        }
        lastEip = startEip + block.Offsets[executed - 1];
        return true;
    }

    // ブロックを先頭からデコードして IR に下ろす。対象外の命令で始まるなら null。
    // 分岐を含めたらそこで終わる。命令がページ末尾の 15 バイトに掛かる前に止める(デコードが隣のページを読まないため)。
    static TranslatedBlock Lower(EmuEnvironment env, CPU cpu, uint physical)
    {
        var instructions = new List<Block>();
        var offsets = new List<uint>();
        var offset = 0u;
        var pageRemaining = 0x1000 - (physical & 0xFFF);
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
            if (Semantics.IsBranch(instruction.Opcode)) break;
        }
        return instructions.Count == 0 ? null : new TranslatedBlock([.. instructions], [.. offsets]);
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
