using static Emu86.Ext;

namespace Emu86;

public struct DecodeContext
{
    public bool Cs, Es, Ss, Ds, Fs, Gs, OperandSize, AddressSize, Lock;
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

    public (bool IsSuccess, Unit value, CPU cpu, string log) Step(EmuEnvironment env, CPU cpu) =>
        Execute(env, cpu, decoded, useFast);

    public (bool IsSuccess, Unit value, CPU cpu, string log) Execute(EmuEnvironment env, CPU cpu, State<Unit> instruction) =>
        Execute(env, cpu, instruction, false);

    (bool IsSuccess, Unit value, CPU cpu, string log) Execute(EmuEnvironment env, CPU cpu, State<Unit> instruction, bool tryFast)
    {
        cpu.Decode = default;
        cpu.CopyTo(checkpoint);
        UsedFastPath = false;
        try
        {
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
        catch (PageFaultException)
        {
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
