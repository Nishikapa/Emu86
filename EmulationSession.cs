using static Emu86.CPU;

namespace Emu86;

// 起動から停止までの環境の所有者。ゲストがリセットしても、例外時の破棄先は常に最新の環境になる。
internal sealed class EmulationSession : IDisposable
{
    readonly RunOptions options;
    readonly DiskConfiguration disk;
    CPU cpu;

    public EmuEnvironment Environment { get; private set; }
    public long Count { get; private set; }

    public EmulationSession(RunOptions options, DiskConfiguration disk = null)
    {
        this.options = options;
        this.disk = disk;
        Environment = EnvironmentFactory.Create(disk, attachDisk: !options.Resume, options.CdromPath, options.RamBytes,
            options.InstructionsPerSecond, options.CompileAfter);
        try
        {
            AttachSerial();
            if (options.Resume)
            {
                (var count, cpu) = SnapshotStore.Load(options.SnapshotPath, Environment, disk);
                Count = count;
                Console.WriteLine($"[snapshot] resumed from {options.SnapshotPath} at instruction {Count}");
            }
            else
            {
                cpu = ResetVector();
                Environment.CmosRtc.SetClock(options.Rtc ?? DateTime.Now, 0);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    static CPU ResetVector() => _cs.setter(_ip.setter(new CPU())(0xFFF0))(0xF000);

    void AttachSerial()
    {
        if (options.Com1Pipe != null) Environment.Com1.Host = new NamedPipeSerialHost(options.Com1Pipe);
        if (options.Com2Pipe != null) Environment.Com2.Host = new NamedPipeSerialHost(options.Com2Pipe);
        if (options.Com1Pipe != null || options.Com2Pipe != null)
            Console.Error.WriteLine($"[serial] COM1={PipeName(options.Com1Pipe)} COM2={PipeName(options.Com2Pipe)}");
    }

    static string PipeName(string pipe) => pipe != null ? @"\\.\pipe\" + pipe : "none";

    // リセット要求では環境を作り直し、同じオーバーレイとホスト接続で実行を続ける。
    public void Run(TextWriter trace = null, Action<long, CPU, EmuEnvironment> saveSnapshot = null,
        CancellationToken stop = default, EmulationControl control = null, Action<EmuEnvironment> rebind = null)
    {
        ObjectDisposedException.ThrowIf(Environment == null, this);
        var automation = Automation();
        while (true)
        {
            Count = EmulationRunner.Run(options, Environment, cpu, Count, trace, saveSnapshot: saveSnapshot,
                snapshotInterval: options.SnapshotInterval, stop: stop, control: control, bootAutomation: automation);
            if (!Environment.ResetRequested || stop.IsCancellationRequested || Count >= options.Limit) return;
            Console.Error.WriteLine($"[system] reset at instruction {Count}");
            Reset();
            automation = Automation();
            control?.Restart();
            rebind?.Invoke(Environment);
        }
    }

    BootAutomation Automation() => options.BootDebug ? BootAutomation.NtldrDebug()
        : options.Type.Count > 0 ? BootAutomation.Typing(options.Type) : null;

    void Reset()
    {
        var previous = Environment;
        var font = previous.Vga.FallbackFont;
        var hadDisk = previous.Ata != null;
        var cdromPath = previous.Cdrom?.MediaPath;
        var now = previous.CmosRtc.Current((ulong)Count);
        var ips = previous.Clock.InstructionsPerSecond;
        // 同じ媒体を開き直すため、古い環境のハンドルを先に閉じる。作成失敗時も previous はこのセッションが破棄する。
        previous.ReleaseMedia();
        var fresh = EnvironmentFactory.Create(disk, attachDisk: hadDisk, cdromPath, previous.Ram.LongLength,
            ips, options.CompileAfter);
        Environment = fresh;
        try
        {
            fresh.CmosRtc.SetClock(now, (ulong)Count);
            (fresh.Com1.Host, previous.Com1.Host) = (previous.Com1.Host, null);
            (fresh.Com2.Host, previous.Com2.Host) = (previous.Com2.Host, null);
            fresh.Vga.FallbackFont = font;
            cpu = ResetVector();
        }
        finally
        {
            previous.Dispose();
        }
    }

    public void Dispose()
    {
        Environment?.Dispose();
        Environment = null;
    }
}
