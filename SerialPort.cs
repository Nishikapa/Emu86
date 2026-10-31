using System.Collections.Concurrent;
using System.IO.Pipes;

namespace Emu86;

// 16550 互換 UART。送信は即時にホスト側(名前付きパイプ)へ渡し、受信はパイプから届いたバイトを
// 実行スレッドの Pump で受信 FIFO(上限なし)へ移す。割り込みは受信データあり(IER bit0)と
// 送信ホールディング空(IER bit1、THR 書き込み後に 1 回)に対応する。KDCOM(カーネルデバッガ)は
// ポーリングで使い、XP の serial.sys は割り込みで使う。
public sealed class Uart16550
{
    public readonly Queue<byte> Rx = new();
    public byte Ier, Fcr, Lcr, Mcr, Scr, Dll = 1, Dlm;
    public bool ThrePending;
    public ISerialHost Host;

    public bool Dlab => (Lcr & 0x80) != 0;
    public bool IrqAsserted => ((Ier & 1) != 0 && Rx.Count > 0) || ((Ier & 2) != 0 && ThrePending);

    // レジスタ(Lens)の表: オフセット 0-7 の読み出しと書き込み(null なら書けない)。DLAB で意味が変わるものはその場で分ける。
    readonly record struct Register(Func<Uart16550, byte> Read, Action<Uart16550, byte> Write = null);

    static readonly Register[] Registers =
    [
        // 0: RBR/THR(DLAB=1 なら DLL)。送信は即時にホストへ(ループバックなら受信 FIFO へ)、THRE 割り込みを保留する。
        new(uart => uart.Dlab ? uart.Dll : uart.Rx.Count > 0 ? uart.Rx.Dequeue() : (byte)0,
            (uart, value) =>
            {
                if (uart.Dlab) { uart.Dll = value; return; }
                if ((uart.Mcr & 0x10) != 0) uart.Rx.Enqueue(value); else uart.Host?.Send(value);
                if ((uart.Ier & 2) != 0) uart.ThrePending = true;
            }),
        // 1: IER(DLAB=1 なら DLM)
        new(uart => uart.Dlab ? uart.Dlm : uart.Ier,
            (uart, value) =>
            {
                if (uart.Dlab) uart.Dlm = value;
                else { uart.Ier = (byte)(value & 0x0F); if ((uart.Ier & 2) != 0) uart.ThrePending = true; }
            }),
        // 2: IIR(読み出し: 優先度順に受信データ > THRE。THRE 割り込みは読むと取り下げる)/ FCR(書き込み: bit1 で受信 FIFO を消す)
        new(uart =>
            {
                var fifo = (uart.Fcr & 1) != 0 ? 0xC0 : 0x00;
                if ((uart.Ier & 1) != 0 && uart.Rx.Count > 0) return (byte)(fifo | 0x04);
                if ((uart.Ier & 2) != 0 && uart.ThrePending) { uart.ThrePending = false; return (byte)(fifo | 0x02); }
                return (byte)(fifo | 0x01);
            },
            (uart, value) => { uart.Fcr = value; if ((value & 0x02) != 0) uart.Rx.Clear(); }),
        new(uart => uart.Lcr, (uart, value) => uart.Lcr = value),                   // 3: LCR
        new(uart => uart.Mcr, (uart, value) => uart.Mcr = (byte)(value & 0x1F)),    // 4: MCR
        new(uart => (byte)(0x60 | (uart.Rx.Count > 0 ? 0x01 : 0x00))),             // 5: LSR: THRE | TEMT | DR
        // 6: MSR: ループバック(MCR bit4)では RTS→CTS、DTR→DSR、OUT1→RI、OUT2→DCD を映す(KDCOM のポート存在検査はこれを見る)。
        //    通常時は CTS/DSR/DCD を立てておく。
        new(uart => (uart.Mcr & 0x10) != 0
            ? (byte)(((uart.Mcr & 0x02) != 0 ? 0x10 : 0) | ((uart.Mcr & 0x01) != 0 ? 0x20 : 0) | ((uart.Mcr & 0x04) != 0 ? 0x40 : 0) | ((uart.Mcr & 0x08) != 0 ? 0x80 : 0))
            : (byte)0xB0),
        new(uart => uart.Scr, (uart, value) => uart.Scr = value),                   // 7: SCR
    ];

    public byte Read(int offset) => offset is >= 0 and < 8 ? Registers[offset].Read(this) : (byte)0xFF;

    public void Write(int offset, byte value)
    {
        if (offset is >= 0 and < 8) Registers[offset].Write?.Invoke(this, value);
    }

    // 実行スレッドから: ホストから届いたバイトを受信 FIFO へ移し、送信バッファを流す。
    public void Pump()
    {
        if (Host == null) return;
        while (Host.TryReceive(out var value)) Rx.Enqueue(value);
        Host.Flush();
    }

    // スナップショット(v10 から)。
    static readonly Codec<Uart16550> Layout = new Codec<Uart16550>()
        .ByteQueue(u => u.Rx, 1 << 20)
        .U8(u => u.Ier, (u, v) => u.Ier = v)
        .U8(u => u.Fcr, (u, v) => u.Fcr = v)
        .U8(u => u.Lcr, (u, v) => u.Lcr = v)
        .U8(u => u.Mcr, (u, v) => u.Mcr = v)
        .U8(u => u.Scr, (u, v) => u.Scr = v)
        .U8(u => u.Dll, (u, v) => u.Dll = v)
        .U8(u => u.Dlm, (u, v) => u.Dlm = v)
        .Bool(u => u.ThrePending, (u, v) => u.ThrePending = v);

    public void SaveState(BinaryWriter writer) => Layout.Save(writer, this);

    public void LoadState(BinaryReader reader) => Layout.Load(reader, this, SnapshotStore.Version);
}

// UART の相手側(ホスト)。送信バイトを受け取り、受信バイトを返す。
public interface ISerialHost : IDisposable
{
    void Send(byte value);
    bool TryReceive(out byte value);
    void Flush();
}

// 名前付きパイプ(\\.\pipe\<name>)のサーバー。WinDbg は "com:pipe,port=\\.\pipe\<name>" で接続する。
// クライアントが切断したら再び接続を待つ。未接続のあいだの送信は捨てる。
public sealed class NamedPipeSerialHost : ISerialHost
{
    readonly string name;
    readonly ConcurrentQueue<byte> incoming = new();
    readonly List<byte> outgoing = new();
    readonly Lock gate = new();
    readonly CancellationTokenSource cancel = new();
    readonly SemaphoreSlim pending = new(0);
    NamedPipeServerStream pipe;
    volatile bool connected;

    public string Name => name;
    public bool Connected => connected;

    public NamedPipeSerialHost(string name)
    {
        this.name = name;
        _ = Task.Run(ServeAsync);
        _ = Task.Run(WriteAsync);
    }

    // 送信は専用タスクが行う。パイプの Write は相手が読むまでブロックしうるので、実行スレッドでは行わない。
    async Task WriteAsync()
    {
        while (!cancel.IsCancellationRequested)
        {
            try { await pending.WaitAsync(cancel.Token); }
            catch (OperationCanceledException) { break; }
            byte[] data;
            NamedPipeServerStream target;
            lock (gate)
            {
                data = [.. outgoing];
                outgoing.Clear();
                target = pipe;
            }
            if (data.Length == 0 || target == null) continue;
            try { await target.WriteAsync(data, cancel.Token); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
    }

    async Task ServeAsync()
    {
        var buffer = new byte[4096];
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancel.Token);
                lock (gate) { pipe = server; connected = true; }
                Console.Error.WriteLine($"[serial] client connected to \\\\.\\pipe\\{name}");
                while (!cancel.IsCancellationRequested)
                {
                    var read = await server.ReadAsync(buffer, cancel.Token);
                    if (read <= 0) break;
                    for (var index = 0; index < read; index++) incoming.Enqueue(buffer[index]);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            finally
            {
                lock (gate) { pipe = null; connected = false; }
            }
            if (!cancel.IsCancellationRequested) Console.Error.WriteLine($"[serial] client disconnected from \\\\.\\pipe\\{name}");
        }
    }

    public void Send(byte value)
    {
        lock (gate) outgoing.Add(value);
    }

    public bool TryReceive(out byte value) => incoming.TryDequeue(out value);

    // 実行スレッドから: 溜まった送信バイトを送信タスクへ渡す(ブロックしない)。
    public void Flush()
    {
        lock (gate)
        {
            if (outgoing.Count == 0) return;
            if (pipe == null) { outgoing.Clear(); return; } // 未接続なら捨てる
        }
        if (pending.CurrentCount == 0) pending.Release();
    }

    public void Dispose()
    {
        cancel.Cancel();
        pending.Release();
        lock (gate) { try { pipe?.Dispose(); } catch (IOException) { } pipe = null; }
    }
}
