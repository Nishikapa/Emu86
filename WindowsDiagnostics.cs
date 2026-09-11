using System.Text;
using static Emu86.Ext;

namespace Emu86;

internal static class WindowsDiagnostics
{
    public static bool IsKernelEntryStack(uint stack) => stack is >= 0xc16a8000 and < 0xc16aa000;

    public static uint ReadDword(EmuEnvironment env, uint address) =>
        (uint)(EnvGetMemoryData8(env, address) | (EnvGetMemoryData8(env, address + 1) << 8)
            | (EnvGetMemoryData8(env, address + 2) << 16) | (EnvGetMemoryData8(env, address + 3) << 24));

    public static string Caller(EmuEnvironment env, CPU cpu)
    {
        var frames = new StringBuilder();
        try
        {
            var frame = cpu.ebp;
            for (var depth = 0; depth < 8 && frame != 0; depth++)
            {
                frames.Append(ReadDword(env, frame + 4).ToString("x8")).Append(' ');
                frame = ReadDword(env, frame);
            }
        }
        catch (Exception) { frames.Append('?'); }
        return frames.ToString();
    }

    public static void LogEntry(EmuEnvironment env, CPU cpu, long count, TextWriter output)
    {
        try
        {
            var stack = cpu.esp;
            var returnAddress = ReadDword(env, stack);
            var argument1 = ReadDword(env, stack + 4);
            var argument2 = ReadDword(env, stack + 8);
            var irpInfo = "";
            if (argument2 >= 0x80000000 && argument2 < 0xF0000000 && (ReadDword(env, argument2) & 0xFFFF) == 6)
            {
                var location = ReadDword(env, argument2 + 0x60);
                irpInfo = $" irp major={EnvGetMemoryData8(env, location):x2} minor={EnvGetMemoryData8(env, location + 1):x2}";
            }
            var debugString = "";
            if (argument2 == 0x40010006 && returnAddress >= 0x1000 && returnAddress < 0x80000000)
            {
                var length = (int)Math.Min(ReadDword(env, returnAddress + 0x14), 300);
                var pointer = ReadDword(env, returnAddress + 0x18);
                var message = new StringBuilder();
                for (var offset = 0; offset < length; offset++)
                {
                    var character = EnvGetMemoryData8(env, pointer + (uint)offset);
                    if (character == 0) break;
                    message.Append(character is >= 0x20 and < 0x7f ? (char)character : '.');
                }
                debugString = " dbg=\"" + message + "\"";
            }
            output.WriteLine($"ENTRY {count}: {cpu.eip:x8} -> {cpu.eip:x8} cr3={cpu.cr3:x8} ret={returnAddress:x8} a1={argument1:x8} a2={argument2:x8}{irpInfo}{debugString}");
        }
        catch (Exception) { output.WriteLine($"ENTRY {count}: -> {cpu.eip:x8} (args unreadable)"); }
    }
}
