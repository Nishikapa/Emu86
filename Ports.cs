namespace Emu86;

// I/O ポートの入出力(PortMap の表を引く)。
static public class Ports
{
    // ポートの振り分けは PortMap の表(ポート範囲 → デバイスの読み書き)。port に該当する項のうち、
    // その向きの処理(Read/Write/ReadWide/WriteWide)を持ち、条件(When)も満たす最初のもの(表の順)の処理。無ければ null。
    static T Handler<T>(EmuEnvironment env, int port, Func<PortMapping, T> handler) where T : Delegate
    {
        foreach (var mapping in PortMap.ByPort[port])
            if (handler(mapping) is { } found && mapping.Applies(env, port)) return found;
        return null;
    }

    // 該当する項がなければ IoPort 配列を素通しする。
    static public byte EnvInPort(EmuEnvironment env, int port)
    {
        port &= 0xFFFF;
        return Handler(env, port, mapping => mapping.Read) is { } read ? read(env, port) : env.IoPort[port];
    }

    static public void EnvOutPort(EmuEnvironment env, int port, byte val)
    {
        port &= 0xFFFF;
        if (Handler(env, port, mapping => mapping.Write) is { } write) write(env, port, val);
        else env.IoPort[port] = val;
    }

    // 幅付きポート入出力: 幅付きを自分で扱う項(ATA データ、PCI コンフィグ、ACPI の dword など)があればそれに任せ、
    // なければ 1 バイトずつ(隣接ポートへ波及する)。
    static public uint EnvInPortN(EmuEnvironment env, int port, int size)
    {
        var p = port & 0xFFFF;
        if (Handler(env, p, mapping => mapping.ReadWide) is { } readWide) return readWide(env, p, size);
        uint v = 0;
        for (int i = 0; i < size; i++)
            v |= (uint)EnvInPort(env, port + i) << (8 * i);
        return v;
    }

    static public void EnvOutPortN(EmuEnvironment env, int port, int size, uint val)
    {
        var p = port & 0xFFFF;
        if (Handler(env, p, mapping => mapping.WriteWide) is { } writeWide) { writeWide(env, p, size, val); return; }
        for (int i = 0; i < size; i++)
            EnvOutPort(env, port + i, (byte)(val >> (8 * i)));
    }
}
