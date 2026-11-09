using Emu86;
using static Emu86.Conversions;
using static Emu86.States;
using static Emu86.DataAccess;
using static Emu86.AluStates;
using static Emu86.CpuStates;
using static Assert;

static class StateTests
{
    static void CpuUpdates()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var original = new CPU { eax = 10 };
        var replacement = new CPU { eax = 20 };
        var calls = 0;
        var update = SetCpu(cpu => { Equal(original, cpu, "input CPU"); calls++; return replacement; });
        Equal(0, calls, "CPU update is deferred");
        var result = update(env, original, null);
        Equal(true, result.IsSuccess, "CPU update success");
        Equal(replacement, result.cpu, "replacement CPU returned");
        Equal(string.Empty, result.log, "CPU update log");
        Equal(1, calls, "CPU update runs once");
        var contextual = SetCpu((context, cpu) =>
        {
            Equal(env, context, "environment passed through");
            Equal(original, cpu, "contextual input CPU");
            calls++;
            return replacement;
        });
        Equal(replacement, contextual(env, original, null).cpu, "contextual replacement CPU");
        Equal(2, calls, "contextual update runs once");
        var read = GetDataFromCpu(cpu => { calls++; return cpu.eax; });
        Equal(2, calls, "CPU read is deferred");
        var readResult = read(env, replacement, null);
        Equal(20u, readResult.value, "CPU read value");
        Equal(replacement, readResult.cpu, "CPU read retains instance");
        Equal(3, calls, "CPU read runs once");
        var redirect = new Accessor<CPU, uint>(cpu => cpu.eax, cpu => value =>
        {
            Equal(original, cpu, "batch first CPU");
            replacement.eax = value;
            return replacement;
        });
        var accumulate = new Accessor<CPU, uint>(cpu => cpu.eax, cpu => value =>
        {
            Equal(replacement, cpu, "batch threads replacement CPU");
            cpu.eax += value;
            return cpu;
        });
        var batch = SetCpu((redirect, 100u), (accumulate, 23u));
        Equal(20u, replacement.eax, "batch is deferred");
        Equal(replacement, batch(env, original, null).cpu, "batch CPU result");
        Equal(123u, replacement.eax, "batch updates in order");
        Equal(10u, original.eax, "batch original CPU unchanged");
        Equal(original, SetCpu<uint>()(env, original, null).cpu, "empty batch");
        var failing = new Accessor<CPU, uint>(cpu => cpu.eax, cpu => value => throw new InvalidOperationException());
        Throws<InvalidOperationException>(() => SetCpu((CPU._eax, 42u), (failing, 1u), (CPU._eax, 99u))(env, original, null), "batch stops on exception");
        Equal(42u, original.eax, "completed batch updates retained");
        original.eip = 0;
        Equal(original, IpInc(-1)(env, original, null).cpu, "IP update retains CPU");
        Equal(uint.MaxValue, original.eip, "IP decrement wraps");
        IpInc(2)(env, original, null);
        Equal(1u, original.eip, "IP increment wraps");
    }

    static void OperandWidths()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU();
        for (var type = 0; type < 3; type++)
        {
            var reads = new int[3];
            State<byte> readByte = (context, state, opcodes) => { reads[0]++; return (true, 0x12, state, "byte"); };
            State<ushort> readWord = (context, state, opcodes) => { reads[1]++; return (true, 0x1234, state, "word"); };
            State<uint> readDword = (context, state, opcodes) => { reads[2]++; return (true, 0x12345678, state, "dword"); };
            var result = Choice(type, readByte, readWord, readDword)(env, cpu, null);
            Equal(type, result.value.type, "operand width");
            Equal(new uint[] { 0x12, 0x1234, 0x12345678 }[type], result.value.Value, "operand value");
            Equal(new[] { "byte", "word", "dword" }[type], result.log, "selected state log");
            Equal(cpu, result.cpu, "selected state CPU");
            Equal(1, reads[type], "selected state runs once");
            Equal(1, reads.Sum(), "unselected states do not run");
            Array.Clear(reads);
            var convert = GetTypeData<uint>(type,
                value => { reads[0]++; return (byte)value; },
                value => { reads[1]++; return (ushort)value; },
                value => { reads[2]++; return value; });
            Equal(0, reads.Sum(), "conversion deferred");
            Equal(0x87654321u & Mask(type), convert(0x87654321).Value, "conversion width");
            Equal(1, reads[type], "selected converter runs once");
            Equal(1, reads.Sum(), "unselected converters do not run");
        }
        foreach (var invalid in new[] { -1, 3 })
        {
            Throws<IndexOutOfRangeException>(() => Choice(invalid, ((byte)0).ToState(), ((ushort)0).ToState(), 0u.ToState()), "invalid operand width");
            Throws<IndexOutOfRangeException>(() => GetTypeData<uint>(invalid, value => (byte)value, value => (ushort)value, value => value), "invalid conversion width");
        }
    }

    // ENTER: フレームポインタの幅はオペランドサイズ、スタックポインタの幅は SS の B ビット。32 ビットでは EBP を 4 バイトで積み、
    // LEAVE と対になる(Windows 7 の HAL が 32 ビットコードで ENTER 8,0 を使う。16 ビット幅で積むとスタックが 2 バイトずれる)。
    static void EnterFrames()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        uint Dword(int address) => BitConverter.ToUInt32(env.Ram, address);
        ushort Word(int address) => BitConverter.ToUInt16(env.Ram, address);
        var cpu = new CPU { stack32 = true, esp = 0x8000, ebp = 0x12345678 };
        Enter(8, 0, 2)(env, cpu, null);
        Equal((0x7FF4u, 0x7FFCu, 0x12345678u), (cpu.esp, cpu.ebp, Dword(0x7FFC)), "32-bit ENTER 8,0: EBP pushed as a dword, EBP = frame, ESP below the locals");
        // 外側のフレーム(0x9000)のリンクを 2 個持つ入れ子: level 3 = リンク 2 個 + 自分のフレーム。
        BitConverter.GetBytes(0x11111111u).CopyTo(env.Ram, 0x8FFC); BitConverter.GetBytes(0x22222222u).CopyTo(env.Ram, 0x8FF8);
        cpu = new CPU { stack32 = true, esp = 0x8000, ebp = 0x9000 };
        Enter(0x10, 3, 2)(env, cpu, null);
        Equal((0x7FFCu, 0x7FE0u), (cpu.ebp, cpu.esp), "32-bit ENTER 16,3: frame pointer and stack pointer");
        Equal((0x00009000u, 0x11111111u, 0x22222222u, 0x00007FFCu), (Dword(0x7FFC), Dword(0x7FF8), Dword(0x7FF4), Dword(0x7FF0)), "32-bit ENTER 16,3: saved EBP, two outer links, own frame");
        // 16 ビット(リアルモードの形): BP を 2 バイトで積み、上位 16 ビットには触らない。
        Array.Clear(env.Ram, 0x7F00, 0x100);
        BitConverter.GetBytes((ushort)0x3333).CopyTo(env.Ram, 0x8FFE);
        cpu = new CPU { esp = 0xAAAA8000, ebp = 0xBBBB9000 };
        Enter(4, 2, 1)(env, cpu, null);
        Equal((0xAAAA7FF6u, 0xBBBB7FFEu), (cpu.esp, cpu.ebp), "16-bit ENTER 4,2 leaves the upper halves alone");
        Equal(((ushort)0x9000, (ushort)0x3333, (ushort)0x7FFE), (Word(0x7FFE), Word(0x7FFC), Word(0x7FFA)), "16-bit ENTER 4,2: saved BP, one outer link, own frame");
        // 16 ビットのオペランドサイズ(66h)を 32 ビットスタックで: BP だけを 2 バイトで積み、スタックは ESP で動く。
        cpu = new CPU { stack32 = true, esp = 0x00018000, ebp = 0xCCCC9000 };
        Array.Clear(env.Ram, 0x7F00, 0x100);
        Enter(2, 0, 1)(env, cpu, null);
        Equal((0x00017FFCu, 0xCCCC7FFEu), (cpu.esp, cpu.ebp), "16-bit ENTER on a 32-bit stack");
    }

    // POP m の書き込み先のアドレスに ESP を使う形: ESP を進めた後の値で実効アドレスを求める。pop [esp] は先頭の値を 1 つ上の枠へ移す
    // (Windows の ExInterlockedPushEntrySList は、これで戻り番地を 3 番目の引数の上へ移してから 2 引数の本体へ続く。進める前の ESP で
    // 計算すると戻り番地がずれ、Windows 7 のセットアップが NTFS_FILE_SYSTEM で止まった)。通常コア・IR の解釈器・コンパイル済みで同じ。
    static void PopStackRelative()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        uint Dword(int address) => BitConverter.ToUInt32(env.Ram, address);
        foreach (var mode in new[] { "reference", "interpreted", "compiled" })
        {
            // スタック(0x8000 から)に 3 語を置いて 1 命令実行し、ESP とスタックの 3 語、0x9000 の語を返す。
            (uint esp, uint first, uint second, uint third, uint other) Run(params byte[] code)
            {
                Array.Clear(env.Ram);
                code.CopyTo(env.Ram, 0x100);
                foreach (var (address, value) in new[] { (0x8000, 0xAAAA5555u), (0x8004, 0x22222222u), (0x8008, 0x33333333u) }) BitConverter.GetBytes(value).CopyTo(env.Ram, address);
                var cpu = new CPU { code32 = true, stack32 = true, eip = 0x100, esp = 0x8000, ebx = 0x9000 };
                if (mode == "reference")
                {
                    var result = Instructions.Execute2(env, cpu, null);
                    Equal(true, result.IsSuccess, $"{mode} executes {Convert.ToHexString(code)}");
                    cpu = result.cpu;
                }
                else
                {
                    var block = Semantics.Lower(Decoder.DecodeInstruction(env, cpu));
                    if (mode == "interpreted") Semantics.Interpret(block, env, cpu); else Semantics.Compile(block)(env, cpu);
                }
                Equal(0x100u + (uint)code.Length, cpu.eip, $"{mode} advances past {Convert.ToHexString(code)}");
                return (cpu.esp, Dword(0x8000), Dword(0x8004), Dword(0x8008), Dword(0x9000));
            }
            Equal((0x8004u, 0xAAAA5555u, 0xAAAA5555u, 0x33333333u, 0u), Run(0x8F, 0x04, 0x24), $"{mode}: pop [esp] stores the top one slot up");
            Equal((0x8004u, 0xAAAA5555u, 0x22222222u, 0xAAAA5555u, 0u), Run(0x8F, 0x44, 0x24, 0x04), $"{mode}: pop [esp+4] counts from the incremented ESP");
            Equal((0x8004u, 0xAAAA5555u, 0x22222222u, 0x33333333u, 0xAAAA5555u), Run(0x8F, 0x03), $"{mode}: pop [ebx] does not depend on ESP");
            Equal((0x8002u, 0x55555555u, 0x22222222u, 0x33333333u, 0u), Run(0x66, 0x8F, 0x04, 0x24), $"{mode}: pop word [esp] moves the word up by two bytes");
        }
    }

    // XLAT: AL <- [seg:(E)BX + AL]。32 ビットのアドレスサイズでは EBX 全体(BX 固定だと上位が落ちる)、0x67 で BX + AL の下位 16 ビット、
    // セグメントオーバーライドが効く。通常コアと IR(解釈・コンパイル)で同じ。
    static void Xlat()
    {
        using var env = new EmuEnvironment(memory: new byte[0x20000]);
        foreach (var mode in new[] { "reference", "interpreted", "compiled" })
        {
            byte Run(uint ebx, params byte[] code)
            {
                Array.Clear(env.Ram);
                code.CopyTo(env.Ram, 0x100);
                env.Ram[0x10210] = 0x5A; // [EBX + AL]
                env.Ram[0x00210] = 0xA5; // [BX + AL]
                env.Ram[0x11210] = 0x3C; // [ES:EBX + AL](ES のベースは 0x1000)
                env.Ram[0x00008] = 0xC3; // [(BX + AL) & 0xFFFF](EBX = 0x1FFF8 のとき)
                var cpu = new CPU { code32 = true, stack32 = true, eip = 0x100, esp = 0x8000, ebx = ebx, eax = 0x10, es_base = 0x1000 };
                if (mode == "reference")
                {
                    var result = Instructions.Execute2(env, cpu, null);
                    Equal(true, result.IsSuccess, $"{mode} executes {Convert.ToHexString(code)}");
                    cpu = result.cpu;
                }
                else
                {
                    var block = Semantics.Lower(Decoder.DecodeInstruction(env, cpu));
                    if (mode == "interpreted") Semantics.Interpret(block, env, cpu); else Semantics.Compile(block)(env, cpu);
                }
                Equal(0x100u + (uint)code.Length, cpu.eip, $"{mode} advances past {Convert.ToHexString(code)}");
                Equal(0u, cpu.eax & 0xFFFFFF00, $"{mode} leaves the rest of EAX alone after {Convert.ToHexString(code)}");
                return cpu.al;
            }
            Equal((byte)0x5A, Run(0x10200, 0xD7), $"{mode}: 32-bit address size uses EBX");
            Equal((byte)0xA5, Run(0x10200, 0x67, 0xD7), $"{mode}: 16-bit address size uses BX");
            Equal((byte)0x3C, Run(0x10200, 0x26, 0xD7), $"{mode}: the segment override replaces DS");
            Equal((byte)0xC3, Run(0x1FFF8, 0x67, 0xD7), $"{mode}: BX + AL wraps at 16 bits");
        }
    }

    // マシンステータスワード(CR0 の下位): CLTS は TS だけを落とす。SMSW は CR0 を r/m へ(メモリは 16 ビット、レジスタは
    // オペランドサイズの幅)。LMSW は下位 4 ビット(PE、MP、EM、TS)を読み込み、PE は落とせない。
    static void MachineStatusWord()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        CPU Run(CPU cpu, params byte[] code)
        {
            code.CopyTo(env.Ram, 0x100);
            cpu.eip = 0x100;
            var result = Instructions.Execute2(env, cpu, null);
            Equal(true, result.IsSuccess, "executes " + Convert.ToHexString(code));
            return result.cpu;
        }
        var cpu = new CPU { code32 = true, stack32 = true };
        CPU._cr0.setter(cpu)(0x0001003B); // PE、MP、TS、ET、NE、WP
        cpu = Run(cpu, 0x0F, 0x06);
        Equal(0x00010033u, CPU._cr0.getter(cpu), "CLTS clears TS only");
        cpu.ebx = 0xFFFFFFFF;
        cpu = Run(cpu, 0x0F, 0x01, 0xE3);
        Equal(0x00010033u, cpu.ebx, "SMSW r32 stores CR0");
        cpu.ebx = 0xFFFFFFFF;
        cpu = Run(cpu, 0x66, 0x0F, 0x01, 0xE3);
        Equal(0xFFFF0033u, cpu.ebx, "SMSW r16 stores the low word only");
        cpu.ebx = 0x2000;
        BitConverter.GetBytes(0xEEEEEEEEu).CopyTo(env.Ram, 0x2000);
        cpu = Run(cpu, 0x0F, 0x01, 0x23);
        Equal(0xEEEE0033u, BitConverter.ToUInt32(env.Ram, 0x2000), "SMSW m16 writes two bytes");
        cpu.ebx = 0x0000FFFE; // PE = 0(落とせない)、MP・EM・TS = 1、上位は無視
        cpu = Run(cpu, 0x0F, 0x01, 0xF3);
        Equal(0x0001003Fu, CPU._cr0.getter(cpu), "LMSW loads MP, EM and TS and cannot clear PE");
        BitConverter.GetBytes((ushort)0x0001).CopyTo(env.Ram, 0x2000);
        cpu.ebx = 0x2000;
        cpu = Run(cpu, 0x0F, 0x01, 0x33);
        Equal(0x00010031u, CPU._cr0.getter(cpu), "LMSW m16 clears MP, EM and TS");
    }

    // #NM(ベクタ 7): CR0.EM か CR0.TS が立っていれば x87 命令は実行されずにフォルトになる(OS が FPU の状態を入れ替える合図)。
    // FWAIT は CR0.MP と CR0.TS の両方が立っているときだけ。リアルモードでは検査しない。IR(解釈器とコンパイル済み)の FWAIT も同じ。
    static void DeviceNotAvailable()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        CPU Machine(uint cr0)
        {
            var cpu = new CPU { code32 = (cr0 & 1) != 0, stack32 = (cr0 & 1) != 0, eip = 0x100 };
            CPU._cr0.setter(cpu)(cr0);
            return cpu;
        }
        // 命令を 1 つ実行して、フォルトのベクタ(完了したら null)と FPU スタックの深さを返す。
        (int? vector, int depth) Run(uint cr0, params byte[] code)
        {
            code.CopyTo(env.Ram, 0x100);
            var cpu = Machine(cr0);
            try
            {
                var result = Instructions.Execute2(env, cpu, null);
                Equal(true, result.IsSuccess, "executes " + Convert.ToHexString(code));
                return (null, System.Numerics.BitOperations.PopCount(result.cpu.fpu_valid));
            }
            catch (CpuFaultException exception) { return (exception.Fault.Vector, System.Numerics.BitOperations.PopCount(cpu.fpu_valid)); }
        }
        const uint pe = 1, mp = 2, em = 4, ts = 8;
        Equal(((int?)null, 1), Run(pe, 0xD9, 0xE8), "FLD1 runs while EM and TS are clear");
        Equal(((int?)7, 0), Run(pe | ts, 0xD9, 0xE8), "TS: an x87 instruction raises #NM and changes nothing");
        Equal(((int?)7, 0), Run(pe | em, 0xD9, 0xE8), "EM: an x87 instruction raises #NM");
        Equal(((int?)7, 0), Run(pe | mp | ts, 0xDB, 0xE3), "FNINIT is not exempt");
        Equal(((int?)null, 1), Run(em | ts, 0xD9, 0xE8), "real mode: not checked (faults are delivered through the IDT only)");
        Equal(((int?)null, 0), Run(pe | ts, 0x9B), "FWAIT with TS alone runs");
        Equal(((int?)null, 0), Run(pe | mp | em, 0x9B), "FWAIT with MP and EM runs");
        Equal(((int?)7, 0), Run(pe | mp | ts, 0x9B), "FWAIT with MP and TS raises #NM");
        Equal((7, false, "#NM"), (Fault.DeviceNotAvailable.Vector, Fault.DeviceNotAvailable.ErrorCode.HasValue, Fault.DeviceNotAvailable.Name), "#NM is vector 7 without an error code");
        // IR: FWAIT は通常コアの State を呼ぶ文になり、同じ条件でフォルトする。
        new byte[] { 0x9B }.CopyTo(env.Ram, 0x100);
        var block = Semantics.Lower(Decoder.DecodeInstruction(env, Machine(pe)));
        Equal(true, block is { Statements: [StateStmt { Name: "FWAIT" }] }, "FWAIT lowers to the reference core's state");
        int? Vector(Action<CPU> run, uint cr0)
        {
            var cpu = Machine(cr0);
            try { run(cpu); return cpu.eip == 0x101 ? null : -1; }
            catch (CpuFaultException exception) { return exception.Fault.Vector; }
        }
        var compiled = Semantics.Compile(block);
        Equal(((int?)null, (int?)7), (Vector(cpu => Semantics.Interpret(block, env, cpu), pe | ts), Vector(cpu => Semantics.Interpret(block, env, cpu), pe | mp | ts)), "interpreted FWAIT");
        Equal(((int?)null, (int?)7), (Vector(cpu => compiled(env, cpu), pe | ts), Vector(cpu => compiled(env, cpu), pe | mp | ts)), "compiled FWAIT");
    }

    static void ControlRegisters()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU();
        foreach (var entry in new[] { (Register: 0, Value: 0x80010001u), (Register: 2, Value: 0x12345678u),
            (Register: 3, Value: 0x9000u), (Register: 4, Value: 0x20u) })
        {
            env.Paging.TagR[0] = env.Paging.TagW[0] = uint.MaxValue;
            var result = SetCrReg(entry.Register, entry.Value)(env, cpu, null);
            Equal(true, result.IsSuccess, "control register write");
            Equal(cpu, result.cpu, "control register CPU");
            Equal(entry.Value, GetCrReg(entry.Register)(env, cpu, null).value, "control register readback");
            Equal(0u, env.Paging.TagR[0], "read TLB flushed");
            Equal(0u, env.Paging.TagW[0], "write TLB flushed");
        }
        Equal(true, env.Paging.Enabled && env.Paging.WriteProtect && env.Paging.Pae, "paging state synchronized");
        Equal(0x9000u, env.Paging.Cr3Base, "page table base synchronized");
        foreach (var invalid in new[] { -1, 1, 5, 8 })
        {
            Throws<KeyNotFoundException>(() => GetCrReg(invalid), "invalid control register read");
            Throws<KeyNotFoundException>(() => SetCrReg(invalid, 0), "invalid control register write");
        }
    }

    static void Combinators()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU { eax = 5 };
        var calls = 0;
        State<uint> read = (context, state, opcodes) => { calls++; return (true, state.eax, state, "read"); };
        var accepted = read.Where(value => value == 5)(env, cpu, null);
        Equal(true, accepted.IsSuccess, "where keeps a matching value");
        Equal(5u, accepted.value, "where passes the value through");
        Equal("read", accepted.log, "where keeps the log");
        var rejected = read.Where(value => value == 6)(env, cpu, null);
        Equal(false, rejected.IsSuccess, "where fails a non-matching value");
        Equal(0u, rejected.value, "where clears the value on failure");
        Equal(cpu, rejected.cpu, "where returns the CPU unchanged");
        Equal(2, calls, "where runs the source once per evaluation");
        State<uint> failing = (context, state, opcodes) => (false, 5, state, string.Empty);
        var tested = 0;
        Equal(false, failing.Where(value => { tested++; return true; })(env, cpu, null).IsSuccess, "where propagates failure");
        Equal(0, tested, "where does not test a failed value");
        Throws<Exception>(() => ((State<uint>)null).Where(value => true), "where rejects a null state");

        // クエリ構文: where 句はガードになり、述語が偽なら以降の from は実行されない。
        var written = 0;
        State<Unit> guarded =
            from value in read
            where value == 5
            from _ in SetCpu(state => { written++; state.ebx = value; return state; })
            select Unit.unit;
        Equal(true, guarded(env, cpu, null).IsSuccess, "query guard passes");
        Equal(5u, cpu.ebx, "query guard runs the following clause");
        cpu.eax = 7;
        Equal(false, guarded(env, cpu, null).IsSuccess, "query guard fails");
        Equal(1, written, "query guard short-circuits the following clause");

        var executed = 0;
        var body = SetCpu(state => { executed++; return state; });
        Equal(true, When(false, body)(env, cpu, null).IsSuccess, "when with a false condition succeeds");
        Equal(cpu, When(false, body)(env, cpu, null).cpu, "when with a false condition keeps the CPU");
        Equal(0, executed, "when with a false condition skips the body");
        When(true, body)(env, cpu, null);
        Equal(1, executed, "when with a true condition runs the body");
        Unless(true, body)(env, cpu, null);
        Equal(1, executed, "unless with a true condition skips the body");
        Unless(false, body)(env, cpu, null);
        Equal(2, executed, "unless with a false condition runs the body");
        Equal(true, ReferenceEquals(When(false, body), Pass), "when reuses the shared no-op");

        // While: 条件が偽なら 0 回、本体の失敗はその時点の CPU で伝わる。
        var ticks = 0;
        cpu.edx = 3;
        var countdown = While(state => state.edx != 0, SetCpu(state => { ticks++; state.edx--; return state; }));
        Equal(0, ticks, "while is deferred");
        var looped = countdown(env, cpu, null);
        Equal((true, 3, 0u), (looped.IsSuccess, ticks, cpu.edx), "while runs until the condition fails");
        Equal(cpu, looped.cpu, "while returns the CPU instance");
        Equal((true, 3), (countdown(env, cpu, null).IsSuccess, ticks), "while with a false condition runs zero times");
        cpu.edx = 5;
        State<Unit> ticking = (context, state, opcodes) => { state.edx--; return (state.edx != 3, Unit.unit, state, "tick"); };
        var aborted = While(state => state.edx != 0, ticking)(env, cpu, null);
        Equal((false, 3u, "tick"), (aborted.IsSuccess, cpu.edx, aborted.log), "while stops at the first failing iteration");

        cpu.ecx = 10;
        var modify = CPU._ecx.Modify(value => value * 3);
        Equal(10u, cpu.ecx, "modify is deferred");
        Equal(cpu, modify(env, cpu, null).cpu, "modify returns the same CPU instance");
        Equal(30u, cpu.ecx, "modify applies the update");
        cpu.cf = true;
        CPU._cf.Modify(flag => !flag)(env, cpu, null);
        Equal(false, cpu.cf, "modify toggles a flag");
    }

    // 手順書モナド(Script): 実行するまで何もしない、待ちで中断して続きから再開する、クエリ構文での合成、
    // Then/While/ForEach が続きを積み重ねない(長い繰り返しでも再開のたびの呼び出しの深さが変わらない)。
    static void ScriptMonad()
    {
        // 待ち条件が満たされている前提で最後まで進め、値と中断した回数を返す(BootAutomation.Step と同じ進め方)。
        static (T value, int waits) Drive<T>(Script<T> script)
        {
            for (var waits = 0; ; waits++)
            {
                var progress = script();
                if (progress.Completed) return (progress.Value, waits);
                if (!progress.Ready()) throw new Exception("the wait is not satisfied");
                script = progress.Rest;
            }
        }

        Equal((5, 0), Drive(Scripts.Return(5)), "return completes without waiting");

        var log = new List<string>();
        var gate = false;
        Script<int> script =
            from start in Scripts.Get(() => log.Count)
            from _1 in Scripts.Do(() => log.Add("before"))
            from _2 in Scripts.WaitFor(() => gate)
            from _3 in Scripts.Do(() => log.Add("after"))
            select start + 10;
        Equal(0, log.Count, "a script does nothing until it runs");
        var suspended = script();
        Equal((false, "before"), (suspended.Completed, string.Join(",", log)), "runs up to the first wait");
        Equal(false, suspended.Ready(), "the wait is not satisfied yet");
        gate = true;
        Equal(true, suspended.Ready(), "the wait sees the condition change");
        var finished = suspended.Rest();
        Equal((true, 10, "before,after"), (finished.Completed, finished.Value, string.Join(",", log)), "resumes after the wait and returns the value");
        Equal((12, 1), Drive(script), "a script can run again from the start");

        Equal((7, 1), Drive(Scripts.WaitFor(() => true).Then(Scripts.Return(7))), "then continues with the second script");

        var count = 0;
        var loop = Scripts.While(() => count < 3, Scripts.Do(() => count++).Then(Scripts.WaitFor(() => true)));
        Equal(0, count, "while is deferred");
        Equal((3, 3), (Drive(loop).waits, count), "while repeats until the condition fails");
        Equal(0, Drive(loop).waits, "while with a false condition runs zero times");

        var visited = new List<int>();
        var each = Scripts.ForEach(new[] { 1, 2, 3 }, item =>
            from _1 in Scripts.WaitFor(() => true)
            from _2 in Scripts.Do(() => visited.Add(item))
            select Unit.unit);
        Equal((3, "1,2,3"), (Drive(each).waits, string.Join(",", visited)), "foreach runs the body for each item in order");

        // 20 万回中断しても完了する(繰り返しが続きを積み重ねると、再開のたびに呼び出しが深くなってスタックがあふれる)。
        var total = 0L;
        var many = Scripts.ForEach(Enumerable.Range(1, 200_000), item =>
            from _1 in Scripts.WaitFor(() => true)
            from _2 in Scripts.Do(() => total += item)
            select Unit.unit);
        Equal((200_000, 20_000_100_000L), (Drive(many).waits, total), "a long foreach resumes at constant depth");
        var remaining = 200_000;
        var countdown = Scripts.While(() => remaining > 0, Scripts.Do(() => remaining--).Then(Scripts.WaitFor(() => true)));
        Equal(200_000, Drive(countdown).waits, "a long while resumes at constant depth");
    }

    // 名前付きのケース(ランナーが順に実行し、失敗したケース名を報告する)。
    public static (string Name, Action Run)[] Cases =>
    [
        ("cpu-updates", CpuUpdates),
        ("combinators", Combinators),
        ("operand-widths", OperandWidths),
        ("enter-frames", EnterFrames),
        ("pop-stack-relative", PopStackRelative),
        ("xlat", Xlat),
        ("machine-status-word", MachineStatusWord),
        ("device-not-available", DeviceNotAvailable),
        ("control-registers", ControlRegisters),
        ("script", ScriptMonad),
    ];
}
