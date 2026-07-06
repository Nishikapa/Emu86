using static Emu86.CPU;
using static Emu86.Ext;
using static Emu86.Unit;

namespace Emu86;

struct OpecodeDic
{
    public State<Unit> state;
    public OpecodeDic[] next;
}

static partial class Program
{
    static Dictionary<int, Accessor<CPU, bool>> PrefixStates =>
        new()
        {
            { 0x26, _es_prefix },
            { 0x2E, _cs_prefix },
            { 0x36, _ss_prefix },
            { 0x3E, _ds_prefix },
            { 0x64, _fs_prefix },
            { 0x65, _gs_prefix },
            { 0x66, _operand_size_prefix },
            { 0x67, _address_size_prefix },
            { 0xF0, _lock_prefix }
        };

    // 毎命令の再構築を避けるため、オペコードテーブルは static readonly でキャッシュする。
    static readonly OpecodeDic[] dicPrefixes =
        [.. Enumerable.Range(0, 256).Select(
            index => PrefixStates.TryGetValue(index, out var acc) ?
                new OpecodeDic() { state = acc.Set(true) } :
                default
        )];

    static State<Unit> ClearPrefixes =>
        PrefixStates.Values
        .Select(acc => acc.Set(false))
        .Sequence()
        .Ignore();

    static (byte ope, int len, State<Unit> state)[] OneByteStates =>
    [
        (0x00, 6, Arithmetic),
        (0x06, 1, PushSreg),   // PUSH ES
        (0x07, 1, PopSreg),    // POP ES
        (0x08, 6, Arithmetic),
        (0x0E, 1, PushSreg),   // PUSH CS
        (0x10, 6, Arithmetic),
        (0x16, 1, PushSreg),   // PUSH SS
        (0x17, 1, PopSreg),    // POP SS
        (0x18, 6, Arithmetic),
        (0x1E, 1, PushSreg),   // PUSH DS
        (0x1F, 1, PopSreg),    // POP DS
        (0x20, 6, Arithmetic),
        (0x28, 6, Arithmetic),
        (0x30, 6, Arithmetic),
        (0x38, 6, Arithmetic),
        (0x40, 8, Inc_40_47),
        (0x48, 8, Dec_48_4F),
        (0x50, 8, Push_50_57),
        (0x58, 8, Pop_58_5F),
        (0x60, 1, Pusha_60),
        (0x61, 1, Popa_61),
        (0x6C, 2, Ins_6C_6D),
        (0x6E, 2, Outs_6E_6F),
        (0x68, 1, PushImm_68),
        (0x69, 1, Imul_69_6B),
        (0x6A, 1, PushImm_6A),
        (0x6B, 1, Imul_69_6B),
        (0x70, 16, Jcc_70_7F),
        (0x80, 2, Group1_80_81),
        (0x83, 1, Group1_83),
        (0x84, 2, Test_84_85),
        (0x86, 2, Xchg_86_87),
        (0x88, 4, Mov_88_8B),
        (0x8C, 1, Mov_8C),
        (0x8D, 1, Lea_8D),
        (0x8E, 1, Mov_8E),
        (0x8F, 1, Pop_8F),
        (0x90, 1, Nop_90),
        (0x91, 7, Xchg_91_97),
        (0x9A, 1, CallFar_9A),
        (0x98, 1, Cbw_98),
        (0x99, 1, Cwd_99),
        (0x9B, 1, Fwait_9B),
        (0x9C, 1, Pushf_9C),
        (0x9E, 1, Sahf_9E),
        (0x9F, 1, Lahf_9F),
        (0x9D, 1, Popf_9D),
        (0xA0, 2, Mov_A0_A1),
        (0xA2, 2, Mov_A2_A3),
        (0xA4, 2, Movs_A4_A5),
        (0xA6, 2, Cmps_A6_A7),
        (0xA8, 2, Test_A8_A9),
        (0xAA, 2, Stos_AA_AB),
        (0xAC, 2, Lods_AC_AD),
        (0xAE, 2, Scas_AE_AF),
        (0xB0, 16, Mov_B0_BF),
        (0xC0, 2, Group2_C0_C1),
        (0xC2, 1, Ret_C2),
        (0xC3, 1, Ret_C3),
        (0xC6, 2, Mov_C6_C7),
        (0xC8, 1, Enter_C8),
        (0xC9, 1, Leave_C9),
        (0xCA, 1, Retf_CA),
        (0xCB, 1, Retf_CB),
        (0xCC, 1, Int3_CC),
        (0xCD, 1, Int_CD),
        (0xCE, 1, Into_CE),
        (0xCF, 1, Iret_CF),
        (0xD0, 2, Group2_D0_D1),
        (0xD2, 2, Group2_D2_D3),
        (0xD7, 1, Xlat_D7),
        (0xD8, 8, Fpu_D8_DF),
        (0xE0, 3, Loop_E0_E2),
        (0xE3, 1, Jcxz_E3),
        (0xE4, 2, In_E4_E5),
        (0xE6, 2, Out_E6_E7),
        (0xE8, 1, Call_E8),
        (0xE9, 1, Jump_E9),
        (0xEA, 1, FarJump_EA),
        (0xEB, 1, Jmp_EB),
        (0xEC, 2, In_EC_ED),
        (0xEE, 2, Out_EE_EF),
        (0xF2, 1, Repne_F2),
        (0xF3, 1, Rep_F3),
        (0xF6, 2, Group3_F6_F7),
        (0xF4, 1, Hlt_F4),
        (0xF5, 1, Cmc_F5),
        (0xF8, 1, Clc_F8),
        (0xF9, 1, Stc_F9),
        (0xFA, 1, Cli_FA),
        (0xFB, 1, Sti_FB),
        (0xFC, 1, Cld_FC),
        (0xFD, 1, Std_FD),
        (0xFE, 1, Group4_FE),
        (0xFF, 1, Group5_FF),
    ];

    static (byte ope1, byte ope2, int len, State<Unit> state)[] TwoBytesStates =>
    [
        (0x0F, 0x80, 0x10, Jcc_0F80_0F8F),
        (0x0F, 0x00, 0x01, Group6_0F00),
        (0x0F, 0x01, 0x01, Group7_0F01),
        (0x0F, 0x08, 0x02, CacheInvd_0F08_09),    // INVD / WBINVD (NOP 扱い)
        (0x0F, 0x20, 0x01, Mov_0F20),
        (0x0F, 0x21, 0x01, Mov_0F21),
        (0x0F, 0x23, 0x01, Mov_0F23),
        (0x0F, 0x40, 0x10, Cmov_0F40_4F),         // CMOVcc r, r/m (16 条件)
        (0x0F, 0x22, 0x01, Mov_0F22),
        (0x0F, 0x90, 0x10, Setcc_0F90_9F),        // SETcc r/m8 (16 条件)
        (0x0F, 0xA3, 0x01, BitTest_reg),          // BT  r/m, r
        (0x0F, 0xAB, 0x01, BitTest_reg),          // BTS r/m, r
        (0x0F, 0xB3, 0x01, BitTest_reg),          // BTR r/m, r
        (0x0F, 0xBA, 0x01, Group8_0FBA),          // BT/BTS/BTR/BTC r/m, imm8
        (0x0F, 0xBB, 0x01, BitTest_reg),          // BTC r/m, r
        (0x0F, 0xB6, 0x02, MovzxMovsx_0FB6_BF),   // MOVZX r,r/m8 ; r,r/m16
        (0x0F, 0xBC, 0x02, BitScan_0FBC_BD),      // BSF/BSR r16, r/m16
        (0x0F, 0xBE, 0x02, MovzxMovsx_0FB6_BF),   // MOVSX r,r/m8 ; r,r/m16
        (0x0F, 0xA0, 0x01, PushPopFsGs),          // PUSH FS
        (0x0F, 0xA1, 0x01, PushPopFsGs),          // POP FS
        (0x0F, 0xA8, 0x01, PushPopFsGs),          // PUSH GS
        (0x0F, 0xA9, 0x01, PushPopFsGs),          // POP GS
        (0x0F, 0xA2, 0x01, Cpuid_0FA2),           // CPUID
        (0x0F, 0x30, 0x01, Wrmsr_0F30),           // WRMSR
        (0x0F, 0x31, 0x01, Rdtsc_0F31),           // RDTSC
        (0x0F, 0x32, 0x01, Rdmsr_0F32),           // RDMSR
        (0x0F, 0xB0, 0x02, Cmpxchg_0FB0_B1),      // CMPXCHG r/m, r
        (0x0F, 0xC0, 0x02, Xadd_0FC0_C1),         // XADD r/m, r
        (0x0F, 0xC7, 0x01, Group9_0FC7),          // CMPXCHG8B m64
        (0x0F, 0xC8, 0x08, Bswap_0FC8_CF),        // BSWAP r32
        (0x0F, 0xA4, 0x02, ShldShrd),             // SHLD r/m,r,imm8 ; r/m,r,CL
        (0x0F, 0xAC, 0x02, ShldShrd),             // SHRD r/m,r,imm8 ; r/m,r,CL
        (0x0F, 0xAF, 0x01, Imul_0FAF)             // IMUL r, r/m
    ];

    static readonly Dictionary<int, State<Unit>> oneByte =
        OneByteStates
        .SelectMany(
            item =>
            Enumerable.Range(item.ope, item.len)
            .Select(ope => (ope, item.state))
        )
        .ToDictionary(
            item => item.ope,
            item => item.state
        );

    static readonly Dictionary<int, Dictionary<int, State<Unit>>> twoBytes =
        TwoBytesStates
        .ToLookup(item => item.ope1)
        .ToDictionary(
            item => (int)item.Key,
            item =>
                item.SelectMany(
                    item2 =>
                    Enumerable.Range(item2.ope2, item2.len)
                    .Select(ope => (ope, item2.state))
                )
                .ToDictionary(j => (int)j.ope, j => j.state)
            );

    static readonly OpecodeDic[] dic =
        [.. Enumerable.Range(0, 256).Select(
            index =>
                oneByte.TryGetValue(index, out var state) ? new OpecodeDic() { state = state } :
                twoBytes.TryGetValue(index, out var _dic) ? new OpecodeDic()
                {
                    next = [.. Enumerable.Range(0, 256).Select(
                        index2 =>
                            _dic.TryGetValue(index2, out var state2) ?
                                new OpecodeDic() { state = state2 } :
                                default
                    )]
                } : default
        )];

    // プレフィックスを 0 個以上消費する。
    // CPU が参照型なので「読み進めてから巻き戻す」ことはできない。
    // 先に覗き見(peek)して、プレフィックスと分かったときだけ 1 バイト消費する。
    static public State<Unit> CheckPrefixes => (env, cpu, ope) =>
    {
        while (true)
        {
            var op1 = EnvGetMemoryData8(env, GetCodeAddr(cpu).addr);
            var data1 = dicPrefixes[op1];
            if (default == data1.state)
                return (true, unit, cpu, string.Empty);

            cpu = _eip.setter(cpu)(cpu.eip + 1);
            var (ok, _, cpu2, _) = data1.state(env, cpu, [op1]);
            if (!ok)
                return (false, default, cpu2, string.Empty);
            cpu = cpu2;
        }
    };

    static public State<Unit> Execute => (env, cpu1, ope) =>
    {
        // 未実装オペコードで失敗したとき STOP 診断が正しい位置を指すよう、
        // フェッチ前の EIP を覚えておき、失敗時に戻す(CPU は参照型なので明示的に戻す)。
        var startEip = cpu1.eip;

        var (IsSuccess1, op1, cpu2, log1) = GetMemoryDataIp8(env, cpu1, ope);

        if (!IsSuccess1)
        {
            return (false, default, Rewind(cpu1, startEip), log1);
        }

        var data1 = dic[op1];

        if (default != data1.state)
        {
            var ret = data1.state(env, cpu2, [(byte)op1]);
            return ret.IsSuccess ? ret : (false, default, Rewind(ret.cpu, startEip), ret.log);
        }

        if (default == data1.next)
        {
            return (false, default, Rewind(cpu2, startEip), log1);
        }

        var (IsSuccess2, op2, cpu3, log2) = GetMemoryDataIp8(env, cpu2, ope);

        if (!IsSuccess2)
        {
            return (false, default, Rewind(cpu2, startEip), log1);
        }

        var data2 = data1.next[op2];

        if (default != data2.state)
        {
            var ret = data2.state(env, cpu3, [(byte)op1, (byte)op2]);
            return ret.IsSuccess ? ret : (false, default, Rewind(ret.cpu, startEip), ret.log);
        }

        return (false, default, Rewind(cpu3, startEip), log1 + log2);
    };

    static CPU Rewind(CPU cpu, uint eip) => _eip.setter(cpu)(eip);

    static public State<Unit> Execute2 =>
        from _1 in CheckPrefixes
        from _2 in Execute
        from _3 in ClearPrefixes
        select unit;
}
