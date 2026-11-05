using static Emu86.Unit;

namespace Emu86;

// State で CPU を読み書きする基本操作(SetCpu/GetCpu、Accessor の Get/Set/Modify、EIP の前進、ログ・オペコード列)。
static public class CpuStates
{
    static internal State<T> Get<T>(this Accessor<CPU, T> acc) =>
        GetDataFromCpu(acc.getter);

    static public State<Unit> Set<T>(this Accessor<CPU, T> acc, T value) =>
        SetCpu(cpu => acc.setter(cpu)(value));

    // アクセサが指す値を関数で更新する(CMC の cf = !cf、SP -= n など)。
    static public State<Unit> Modify<T>(this Accessor<CPU, T> acc, Func<T, T> update) =>
        SetCpu(cpu => acc.setter(cpu)(update(acc.getter(cpu))));

    static public State<Unit> IpInc(int inc) =>
        Effect(cpu => cpu.eip = (uint)(cpu.eip + inc));

    static public State<Unit> SetCpu(Func<CPU, CPU> func) =>
        (env, cpu, opcodes) => (true, unit, func(cpu), string.Empty);

    static public State<T> GetDataFromCpu<T>(Func<CPU, T> func) =>
        (env, cpu, opcodes) => (true, func(cpu), cpu, string.Empty);

    static public State<Unit> SetCpu(Func<EmuEnvironment, CPU, CPU> func) =>
        (env, cpu, opcodes) => (true, unit, func(env, cpu), string.Empty);

    // 環境への副作用だけを起こす(ポート出力・MSR・TLB・画面など)。CPU は変えない。
    static public State<Unit> Effect(Action<EmuEnvironment, CPU> action) =>
        (env, cpu, opcodes) => { action(env, cpu); return (true, unit, cpu, string.Empty); };

    // CPU をその場で書き換える(戻り値のない動作。SetCpu(cpu => { ...; return cpu; }) の省略形)。
    static public State<Unit> Effect(Action<CPU> action) =>
        (env, cpu, opcodes) => { action(cpu); return (true, unit, cpu, string.Empty); };

    static public State<Unit> SetCpu<T>(params (Accessor<CPU, T> acc, T value)[] arr) =>
        (env, cpu, opcodes) =>
        {
            foreach (var item in arr)
                cpu = item.acc.setter(cpu)(item.value);
            return (true, unit, cpu, string.Empty);
        };

    static public readonly State<CPU> GetCpu = GetDataFromEnvCpu((env, cpu) => cpu);

    static public State<T> GetDataFromEnvCpu<T>(Func<EmuEnvironment, CPU, T> func) =>                              //無理
        (env, cpu, ope) => (true, func(env, cpu), cpu, string.Empty);

    static public State<Unit> SetCpu(CPU new_cpu) => (env, cpu, ope) => (true, unit, new_cpu, string.Empty); //無理

    static public State<Unit> SetLog(string log) => (env, cpu, ope) => (true, unit, cpu, log);               //無理

    static public readonly State<byte[]> Opecodes = (env, cpu, ope) => (true, ope, cpu, string.Empty);       //無理
}
