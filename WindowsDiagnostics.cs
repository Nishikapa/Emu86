using System.Text;
using static Emu86.Conversions;
using static Emu86.GuestMemory;
using static Emu86.Ports;
using static Emu86.Mmu;

namespace Emu86;

// ゲストメモリの読み取り(Reader モナド): 環境を受け取って値を返す。構造体の読み取りをクエリ構文で合成し、
// ページフォルトなどの例外は Or で既定値に置き換える。診断専用(通常の実行経路では使わない)。
internal delegate T Guest<T>(EmuEnvironment env);

internal static class GuestMemory
{
    public static Guest<T> Return<T>(T value) => _ => value;

    public static Guest<B> Select<A, B>(this Guest<A> reader, Func<A, B> selector) => env => selector(reader(env));

    public static Guest<C> SelectMany<A, B, C>(this Guest<A> reader, Func<A, Guest<B>> selector, Func<A, B, C> projector) => env =>
    {
        var a = reader(env);
        return projector(a, selector(a)(env));
    };

    public static Guest<byte> Byte(uint address) => env => EnvGetMemoryData8(env, address);

    // 線形アドレスの dword(ページング対応の共通経路を 1 バイトずつ通す: ページ境界をまたいでもよい)。
    public static Guest<uint> Dword(uint address) => env =>
        (uint)(EnvGetMemoryData8(env, address) | (EnvGetMemoryData8(env, address + 1) << 8)
            | (EnvGetMemoryData8(env, address + 2) << 16) | (EnvGetMemoryData8(env, address + 3) << 24));

    // NUL 終端までの ASCII 文字列(最大 length 文字、表示できない文字は '.')。
    public static Guest<string> Ascii(uint pointer, int length) => env =>
    {
        var text = new StringBuilder();
        for (var offset = 0; offset < length; offset++)
        {
            var character = EnvGetMemoryData8(env, pointer + (uint)offset);
            if (character == 0) break;
            text.Append(character is >= 0x20 and < 0x7f ? (char)character : '.');
        }
        return text.ToString();
    };

    // 連結リストをたどる: start から step で (値, 次) を読み、次が 0 になるか limit 回まで。
    // 途中で読めなくなったら、そこまでの値と faulted=true を返す。
    public static Guest<(IReadOnlyList<T> values, bool faulted)> Walk<T>(uint start, int limit, Func<uint, Guest<(T value, uint next)>> step) => env =>
    {
        var values = new List<T>();
        try
        {
            for (var current = start; values.Count < limit && current != 0;)
            {
                var (value, next) = step(current)(env);
                values.Add(value);
                current = next;
            }
            return (values, false);
        }
        catch (Exception) { return (values, true); }
    };

    // 読み取り中の例外(ページフォルトなど)を既定値に置き換える。
    public static Guest<T> Or<T>(this Guest<T> reader, T fallback) => env =>
    {
        try { return reader(env); }
        catch (Exception) { return fallback; }
    };
}

internal static class WindowsDiagnostics
{
    public static bool IsKernelEntryStack(uint stack) => stack is >= 0xc16a8000 and < 0xc16aa000;

    public static uint ReadDword(EmuEnvironment env, uint address) => Dword(address)(env);

    // EBP のフレームチェーンをたどった戻り番地(最大 8 段)。読めなくなったところに '?'。
    static Guest<string> Frames(uint frame) =>
        from walk in Walk(frame, 8, f => from returnAddress in Dword(f + 4) from next in Dword(f) select (returnAddress, next))
        select string.Concat(walk.values.Select(address => address.ToString("x8") + " ")) + (walk.faulted ? "?" : "");

    public static string Caller(EmuEnvironment env, CPU cpu) => Frames(cpu.ebp)(env);

    // IRP(カーネル空間のポインタで、先頭の Type が 6)なら現在のスタック位置のメジャー/マイナー機能番号。
    static Guest<string> IrpInfo(uint irp) =>
        irp is < 0x80000000 or >= 0xF0000000
            ? Return("")
            : from type in Dword(irp)
              from info in (type & 0xFFFF) != 6
                  ? Return("")
                  : from location in Dword(irp + 0x60)
                    from major in Byte(location)
                    from minor in Byte(location + 1)
                    select $" irp major={major:x2} minor={minor:x2}"
              select info;

    // DbgPrint 系(a2 = 0x40010006)なら、戻り番地の先にある長さとポインタから文字列を読む。
    static Guest<string> DebugString(uint returnAddress, uint argument2) =>
        argument2 != 0x40010006 || returnAddress is < 0x1000 or >= 0x80000000
            ? Return("")
            : from length in Dword(returnAddress + 0x14)
              from pointer in Dword(returnAddress + 0x18)
              from message in Ascii(pointer, (int)Math.Min(length, 300))
              select " dbg=\"" + message + "\"";

    // 入口の記録: 戻り番地と引数 2 つ、IRP とデバッグ文字列の解釈。
    static Guest<string> Entry(CPU cpu, long count) =>
        from returnAddress in Dword(cpu.esp)
        from argument1 in Dword(cpu.esp + 4)
        from argument2 in Dword(cpu.esp + 8)
        from irpInfo in IrpInfo(argument2)
        from debugString in DebugString(returnAddress, argument2)
        select $"ENTRY {count}: {cpu.eip:x8} -> {cpu.eip:x8} cr3={cpu.cr3:x8} ret={returnAddress:x8} a1={argument1:x8} a2={argument2:x8}{irpInfo}{debugString}";

    public static void LogEntry(EmuEnvironment env, CPU cpu, long count, TextWriter output) =>
        output.WriteLine(Entry(cpu, count).Or($"ENTRY {count}: -> {cpu.eip:x8} (args unreadable)")(env));
}
