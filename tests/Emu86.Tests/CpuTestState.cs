using Emu86;

// CPU の比較は保存形式の本体だけでなく、追加された FPU・TR・LDTR も含める。
// Halted はスナップショットに残さない過渡状態なので、命令実行の比較時だけ含める。
static class CpuTestState
{
    public static string Capture(CPU cpu, bool includeHalted = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        cpu.WriteTo(writer);
        cpu.SaveRuntimeState(writer);
        if (includeHalted) writer.Write(cpu.Halted);
        return Convert.ToHexString(stream.ToArray());
    }
}
