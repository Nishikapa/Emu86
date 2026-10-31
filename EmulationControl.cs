namespace Emu86;

// 実行ループの外(表示ウィンドウなど別スレッド)から中断・再開を要求し、中断中・停止後の CPU 状態を
// 読み出すための窓口。実行スレッドはタイマ周期(IrqPeriod 命令ごと)に WaitIfPaused を呼び、
// 中断要求があればその時点の CPU のコピーを公開してから再開まで待つ。
public sealed class EmulationControl
{
    readonly ManualResetEventSlim resume = new(true);
    readonly Lock gate = new();
    volatile bool pauseRequested;
    CPU published;

    public bool PauseRequested => pauseRequested;
    public bool IsPaused { get; private set; }
    public bool HasStopped { get; private set; }
    public long Count { get; private set; }

    // 中断・再開・停止のたびに実行スレッド上で呼ばれる(UI 側は自分のスレッドへ切り替えること)。
    public event Action StateChanged;

    public void Pause()
    {
        pauseRequested = true;
        resume.Reset();
    }

    public void Resume()
    {
        pauseRequested = false;
        resume.Set();
    }

    // 直近に公開された CPU 状態のコピー(未公開なら null)。
    public CPU Snapshot()
    {
        lock (gate)
        {
            if (published == null) return null;
            var copy = new CPU();
            published.CopyTo(copy);
            return copy;
        }
    }

    void Publish(CPU cpu, long count)
    {
        lock (gate)
        {
            published ??= new CPU();
            cpu.CopyTo(published);
            Count = count;
        }
    }

    // 実行スレッドから: 中断要求があれば状態を公開して再開(または停止要求)まで待つ。
    internal void WaitIfPaused(CPU cpu, long count, CancellationToken stop)
    {
        if (!pauseRequested) return;
        Publish(cpu, count);
        IsPaused = true;
        StateChanged?.Invoke();
        try { resume.Wait(stop); }
        catch (OperationCanceledException) { }
        IsPaused = false;
        StateChanged?.Invoke();
    }

    // 実行スレッドから: 実行ループを抜けたときの最終状態を公開する。
    internal void Stopped(CPU cpu, long count)
    {
        Publish(cpu, count);
        IsPaused = false;
        HasStopped = true;
        StateChanged?.Invoke();
    }

    // 実行スレッドから: システムリセット後に新しい環境で実行を続けるとき、停止状態を取り消す。
    internal void Restart()
    {
        HasStopped = false;
        StateChanged?.Invoke();
    }
}
