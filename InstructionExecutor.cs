using static Emu86.Ext;

namespace Emu86;

public struct DecodeContext
{
    // 現在の命令のプレフィックス(両コアが Prefixes パーサで読む)。
    public Prefix Prefixes;
    public bool Cs { readonly get => Has(Prefix.Cs); set => Set(Prefix.Cs, value); }
    public bool Es { readonly get => Has(Prefix.Es); set => Set(Prefix.Es, value); }
    public bool Ss { readonly get => Has(Prefix.Ss); set => Set(Prefix.Ss, value); }
    public bool Ds { readonly get => Has(Prefix.Ds); set => Set(Prefix.Ds, value); }
    public bool Fs { readonly get => Has(Prefix.Fs); set => Set(Prefix.Fs, value); }
    public bool Gs { readonly get => Has(Prefix.Gs); set => Set(Prefix.Gs, value); }
    public bool OperandSize { readonly get => Has(Prefix.OperandSize); set => Set(Prefix.OperandSize, value); }
    public bool AddressSize { readonly get => Has(Prefix.AddressSize); set => Set(Prefix.AddressSize, value); }
    public bool Lock { readonly get => Has(Prefix.Lock); set => Set(Prefix.Lock, value); }
    readonly bool Has(Prefix prefix) => (Prefixes & prefix) != 0;
    void Set(Prefix prefix, bool value) => Prefixes = value ? Prefixes | prefix : Prefixes & ~prefix;

    bool hasRepeatProgress;
    uint repeatCount, repeatSource, repeatDestination, repeatAccumulator, repeatFlags;

    public void CommitRepeat(CPU cpu)
    {
        hasRepeatProgress = true;
        repeatCount = cpu.ecx;
        repeatSource = cpu.esi;
        repeatDestination = cpu.edi;
        repeatAccumulator = cpu.eax;
        repeatFlags = cpu.eflags;
    }

    internal void RestoreRepeat(CPU cpu)
    {
        if (!hasRepeatProgress) return;
        cpu.ecx = repeatCount;
        cpu.esi = repeatSource;
        cpu.edi = repeatDestination;
        cpu.eax = repeatAccumulator;
        cpu.eflags = repeatFlags;
    }
}

public sealed class InstructionExecutor(bool useFast = true)
{
    readonly CPU checkpoint = new();
    readonly State<Unit> decoded = Program.ExecuteDecoded;
    public bool UsedFastPath { get; private set; }
    // 翻訳キャッシュのコンパイル済みデリゲートで実行した(UsedFastPath も真)。
    public bool UsedTranslation { get; private set; }

    public (bool IsSuccess, Unit value, CPU cpu, string log) Step(EmuEnvironment env, CPU cpu) =>
        Execute(env, cpu, decoded, useFast);

    public (bool IsSuccess, Unit value, CPU cpu, string log) Execute(EmuEnvironment env, CPU cpu, State<Unit> instruction) =>
        Execute(env, cpu, instruction, false);

    (bool IsSuccess, Unit value, CPU cpu, string log) Execute(EmuEnvironment env, CPU cpu, State<Unit> instruction, bool tryFast)
    {
        cpu.Decode = default;
        cpu.CopyTo(checkpoint);
        UsedFastPath = false;
        UsedTranslation = false;
        try
        {
            // 翻訳済みの命令はコンパイル済みデリゲートで、次に高速コアで、未対応の命令だけモナド版で実行する。
            if (tryFast && env.Translations.Enabled && env.Translations.TryRun(env, cpu))
            {
                UsedFastPath = true;
                UsedTranslation = true;
                return (true, Unit.unit, cpu, string.Empty);
            }
            if (tryFast && FastStep(env, cpu))
            {
                UsedFastPath = true;
                return (true, Unit.unit, cpu, string.Empty);
            }
            var result = instruction(env, cpu, null);
            if (result.IsSuccess && !ReferenceEquals(result.cpu, cpu)) result.cpu.CopyTo(cpu);
            if (!result.IsSuccess) Restore(env, cpu, false);
            return (result.IsSuccess, result.value, cpu, result.log);
        }
        catch (CpuFaultException)
        {
            // フォルト(#PF/#GP)は命令開始時の状態へ戻す。REP は完了済みの反復を保って再開する。
            Restore(env, cpu, true);
            throw;
        }
        catch
        {
            Restore(env, cpu, false);
            throw;
        }
        finally
        {
            cpu.Decode = default;
        }
    }

    void Restore(EmuEnvironment env, CPU cpu, bool resumeRepeat)
    {
        var decode = cpu.Decode;
        // ページング関連の CR が変わっていない限り EnvSyncPaging(= TLB 全消去)は呼ばない。
        // Windows は要求時ページングで #PF が頻発するため、フォルトのたびに TLB を捨てると
        // 定常速度が 3 割以上落ちる。CR の変更はメモリオペランドを持たずフォルトしないので、
        // 命令途中のフォルトで CR が食い違うことは通常ない。
        var pagingChanged = cpu.pg != checkpoint.pg || cpu.wp != checkpoint.wp || cpu.cr3 != checkpoint.cr3 || cpu.cr4 != checkpoint.cr4;
        checkpoint.CopyTo(cpu);
        if (resumeRepeat) decode.RestoreRepeat(cpu);
        if (pagingChanged) EnvSyncPaging(env, cpu);
    }
}
