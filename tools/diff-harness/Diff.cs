using System.Security.Cryptography;
using Emu86;

// 差分実行ハーネス(README.md)。同じ命令列・同じ初期状態を通常コア(Instructions.Execute2 = 翻訳なしの InstructionExecutor)で
// 1 命令だけ実行し、CPU(スナップショット形式。FPU スタックと TR/LDTR を含む)・メモリ・I/O ポート・MSR・DR の結果を 1 ケース 1 行で書き出す。
// 続けて、特権レベルと記述子の内容で分かれる場面(Scenes)を同じ形の行で、末尾には、命令の実行ではない結果
// (ROM、PCI コンフィグ空間とその読み書き、モード設定後の BIOS データ領域、INT 10h のサービス、IDENTIFY)を "table ..." の行で足す。
// Compare.ps1 が基準コミットの本体と作業ツリーの本体に対してこれを 2 回ビルド・実行し、出力を比べる。
// 使い方: Emu86.Tests.exe [出力ファイル](省略時は標準出力へ)
static class Diff
{
    static readonly State<Unit> Step = Instructions.Execute2;

    static int Main(string[] args)
    {
        var lines = (
            from mode in new[] { "real", "pm32" }
            from preset in new[] { 0, 1 }
            from code in Cases(mode)
            select Run(mode, preset, code)).Concat(Scenes()).Concat(Tables()).ToList();
        if (args.Length == 0)
        {
            foreach (var line in lines) Console.WriteLine(line);
            return 0;
        }
        File.WriteAllLines(args[0], lines);
        Console.WriteLine($"{lines.Count} cases -> {args[0]}");
        return 0;
    }

    // 命令列。mode で ModRM のメモリ形式([bx] か [ebx])と変位の幅が変わる。
    static IEnumerable<byte[]> Cases(string mode)
    {
        var m32 = mode == "pm32";
        // [bx] (16 ビット: mod=0 rm=7) / [ebx] (32 ビット: mod=0 rm=3)
        byte Mem(int reg) => (byte)((reg << 3) | (m32 ? 3 : 7));
        // [bx+disp8] / [ebx+disp8] (mod=1)
        byte[] Mem8(int reg, byte disp) => [(byte)(0x40 | (reg << 3) | (m32 ? 3 : 7)), disp];
        byte[] Disp16or32(uint v) => m32 ? [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)] : [(byte)v, (byte)(v >> 8)];
        // [bx+disp16] / [ebx+disp32] (mod=2)
        byte[] MemDisp(int reg, uint disp) => [(byte)(0x80 | (reg << 3) | (m32 ? 3 : 7)), .. Disp16or32(disp)];

        List<byte[]> list =
        [
            // far 制御転送
            [0x9A, 0x00, 0x10, 0x08, 0x00], [0x66, 0x9A, 0x00, 0x10, 0x00, 0x00, 0x08, 0x00],
            [0xEA, 0x00, 0x10, 0x08, 0x00], [0x66, 0xEA, 0x00, 0x10, 0x00, 0x00, 0x08, 0x00],
            [0xCB], [0x66, 0xCB], [0xCA, 0x04, 0x00], [0x66, 0xCA, 0x04, 0x00],
            [0xC3], [0x66, 0xC3], [0xC2, 0x04, 0x00], [0x66, 0xC2, 0x04, 0x00],
            [0xFF, Mem(3)], [0xFF, Mem(5)], [0xFF, Mem(2)], [0xFF, Mem(4)], [0xFF, Mem(6)], [0xFF, Mem(0)], [0xFF, Mem(1)],
            [0x66, 0xFF, Mem(3)], [0x66, 0xFF, Mem(5)], [0x66, 0xFF, Mem(2)], [0x66, 0xFF, Mem(4)], [0x66, 0xFF, Mem(6)],
            [0xFF, 0xD3], [0xFF, 0xE3], [0xFF, 0xF3], [0xFF, 0xC3], [0xFF, 0xCB], [0xFE, 0xC3], [0xFE, 0xCB],
            // PUSHA/POPA、LES/LDS/LSS/LFS/LGS、セグメントの push/pop
            [0x60], [0x61], [0x66, 0x60], [0x66, 0x61],
            [0xC4, Mem(0)], [0xC5, Mem(0)], [0x0F, 0xB2, Mem(0)], [0x0F, 0xB4, Mem(0)], [0x0F, 0xB5, Mem(0)],
            [0x66, 0xC4, Mem(0)], [0x66, 0x0F, 0xB2, Mem(0)], [0xC4, 0xC3],
            [0x0F, 0xA0], [0x0F, 0xA1], [0x0F, 0xA8], [0x0F, 0xA9], [0x66, 0x0F, 0xA0], [0x66, 0x0F, 0xA1],
            [0x06], [0x07], [0x0E], [0x16], [0x17], [0x1E], [0x1F], [0x66, 0x06], [0x66, 0x07], [0x66, 0x1E], [0x66, 0x1F],
            [0x8C, 0xD8], [0x8C, Mem(3)], [0x8E, 0xD8], [0x8E, 0xC0],
            // I/O
            [0xE4, 0x10], [0xE5, 0x10], [0x66, 0xE5, 0x10], [0xEC], [0xED], [0x66, 0xED],
            [0xE6, 0x10], [0xE7, 0x10], [0x66, 0xE7, 0x10], [0xEE], [0xEF], [0x66, 0xEF],
            // 分岐
            [0x70, 0x20], [0x71, 0x20], [0x74, 0x20], [0x75, 0x20], [0x7C, 0x20], [0x7F, 0x20], [0x78, 0xF0],
            [0x0F, 0x84, .. Disp16or32(0x20)], [0x0F, 0x85, .. Disp16or32(0xFFF0)], [0x66, 0x0F, 0x84, .. (m32 ? new byte[] { 0x20, 0x00 } : [0x20, 0x00, 0x00, 0x00])],
            [0xEB, 0x10], [0xEB, 0xF0], [0xE9, .. Disp16or32(0x10)], [0xE9, .. Disp16or32(0xFFF0)], [0x66, 0xE9, .. (m32 ? new byte[] { 0x10, 0x00 } : [0x10, 0x00, 0x00, 0x00])],
            [0xE0, 0x10], [0xE1, 0x10], [0xE2, 0x10], [0xE3, 0x10], [0xE2, 0xF0],
            [0xE8, .. Disp16or32(0x10)], [0x66, 0xE8, .. (m32 ? new byte[] { 0x10, 0x00 } : [0x10, 0x00, 0x00, 0x00])],
            // フラグ・システム
            [0x9C], [0x66, 0x9C], [0x9D], [0x66, 0x9D],
            [0x0F, 0x31], [0x0F, 0x32], [0x0F, 0x30], [0x0F, 0x21, 0xC3], [0x0F, 0x23, 0xC3], [0x0F, 0x21, 0xFB], [0x0F, 0x23, 0xF8],
            [0x0F, 0x20, 0xC0], [0x0F, 0x20, 0xD8], [0x0F, 0x22, 0xD8], [0x0F, 0x01, Mem(7)],
            // PUSH/POP r、imm
            [0x50], [0x51], [0x52], [0x53], [0x54], [0x55], [0x56], [0x57], [0x58], [0x59], [0x5A], [0x5B], [0x5C], [0x5D], [0x5E], [0x5F],
            [0x66, 0x50], [0x66, 0x54], [0x66, 0x58], [0x66, 0x5C], [0x8F, Mem(0)], [0x8F, 0xC3], [0x66, 0x8F, Mem(0)], [0x8F, 0xCB],
            // POP m のアドレスに ESP を使う形(pop [esp]、pop [esp+4]。リアルモードでは 67h で 32 ビットアドレッシングにする)
            m32 ? new byte[] { 0x8F, 0x04, 0x24 } : [0x67, 0x8F, 0x04, 0x24], m32 ? new byte[] { 0x8F, 0x44, 0x24, 0x04 } : [0x67, 0x8F, 0x44, 0x24, 0x04],
            [0x68, 0x78, 0x56, 0x34, 0x12], [0x66, 0x68, 0x34, 0x12], [0x6A, 0x80], [0x6A, 0x7F], [0x66, 0x6A, 0x80],
            // TEST/XCHG/INC/DEC
            [0x84, 0xC3], [0x85, 0xC3], [0x66, 0x85, 0xC3], [0x85, Mem(0)], [0x84, Mem(4)], [0xA8, 0x0F], [0xA9, 0x00, 0x00, 0x00, 0x80], [0x66, 0xA9, 0x00, 0x80],
            [0x86, 0xC3], [0x87, 0xC3], [0x87, Mem(0)], [0x66, 0x87, Mem(0)], [0x86, Mem(4)], [0x91], [0x66, 0x93],
            [0x40], [0x41], [0x44], [0x47], [0x48], [0x4B], [0x4C], [0x4F], [0x66, 0x40], [0x66, 0x48], [0x66, 0x44],
            // 乗除算
            [0x0F, 0xAF, 0xC3], [0x0F, 0xAF, Mem(0)], [0x66, 0x0F, 0xAF, 0xC3], [0x66, 0x0F, 0xAF, Mem(0)],
            [0x69, 0xC3, 0x78, 0x56, 0x34, 0x12], [0x69, 0xC3, 0x00, 0x00, 0x01, 0x00], [0x6B, 0xC3, 0x80], [0x6B, 0xC3, 0x02], [0x66, 0x69, 0xC3, 0x00, 0x80], [0x66, 0x6B, 0xC3, 0x7F], [0x6B, Mem(0), 0xFF],
            [0xF6, 0xE3], [0xF7, 0xE3], [0x66, 0xF7, 0xE3], [0xF6, 0xEB], [0xF7, 0xEB], [0x66, 0xF7, 0xEB], [0xF7, Mem(4)], [0xF7, Mem(5)], [0xF6, 0xE7], [0xF6, 0xEF],
            [0xF6, 0xC3, 0x80], [0xF7, 0xC3, 0x00, 0x00, 0x00, 0x80], [0x66, 0xF7, 0xC3, 0x00, 0x80], [0xF7, Mem(0), 0x01, 0x00, 0x00, 0x80], [0xF7, 0xD3], [0xF7, 0xDB], [0xF6, 0xDB], [0x66, 0xF7, 0xDB],
            [0xF6, 0xF3], [0xF7, 0xF3], [0x66, 0xF7, 0xF3], [0xF6, 0xFB], [0xF7, 0xFB], [0x66, 0xF7, 0xFB],
            // Group1、シフト
            [0x80, 0xC3, 0x7F], [0x81, 0xC3, 0x00, 0x00, 0x00, 0x80], [0x83, 0xC3, 0x80], [0x66, 0x81, 0xC3, 0x00, 0x80], [0x66, 0x83, 0xC3, 0x80], [0x80, Mem(0), 0x7F], [0x83, Mem(0), 0x80], [0x83, Mem(7), 0x01], [0x82, 0xC3, 0x01],
            [0xC0, 0xE0, 0x04], [0xC1, 0xE0, 0x04], [0xD0, 0xE0], [0xD1, 0xE0], [0xD2, 0xE0], [0xD3, 0xE0], [0x66, 0xC1, 0xE0, 0x04], [0x66, 0xD3, 0xE8], [0xC1, Mem(0), 0x03], [0xD1, Mem(0)], [0xD3, Mem(2)], [0xC0, Mem(1), 0x01], [0xC1, 0xF8, 0x1F], [0xD3, 0xD3],
            // MOV
            [0xA0, .. Disp16or32(0x3000)], [0xA1, .. Disp16or32(0x3000)], [0xA2, .. Disp16or32(0x3000)], [0xA3, .. Disp16or32(0x3000)], [0x66, 0xA1, .. Disp16or32(0x3000)], [0x66, 0xA3, .. Disp16or32(0x3000)],
            [0xC6, Mem(0), 0x55], [0xC7, Mem(0), 0x78, 0x56, 0x34, 0x12], [0x66, 0xC7, Mem(0), 0x34, 0x12], [0xC6, 0xC3, 0x55], [0xC7, 0xC3, 0x78, 0x56, 0x34, 0x12],
            [0x88, 0xC3], [0x89, 0xC3], [0x8A, 0xC3], [0x8B, Mem(0)], [0x66, 0x89, Mem(0)], [0x88, Mem(4)], [0x8D, Mem(0)], [0x66, 0x8D, Mem(0)],
            [0xB0, 0x55], [0xB8, 0x78, 0x56, 0x34, 0x12], [0x66, 0xB8, 0x34, 0x12],
            // CMPXCHG/XADD/BT
            [0x0F, 0xB0, 0xC3], [0x0F, 0xB1, 0xC3], [0x0F, 0xB1, Mem(0)], [0x66, 0x0F, 0xB1, 0xC3], [0x0F, 0xB1, 0xD8],
            [0x0F, 0xC0, 0xC3], [0x0F, 0xC1, 0xC3], [0x0F, 0xC1, Mem(0)], [0x66, 0x0F, 0xC1, 0xC3],
            [0x0F, 0xA3, Mem(0)], [0x0F, 0xAB, Mem(0)], [0x0F, 0xB3, Mem(0)], [0x0F, 0xBB, Mem(0)], [0x66, 0x0F, 0xA3, Mem(0)], [0x0F, 0xA3, 0xC3], [0x0F, 0xBA, 0xE3, 0x05], [0x0F, 0xBA, Mem(6), 0x21],
            [0x0F, 0xBC, 0xC3], [0x0F, 0xBD, 0xC3], [0x0F, 0xB6, 0xC3], [0x0F, 0xBF, Mem(0)], [0x0F, 0x94, 0xC0], [0x0F, 0x4C, 0xC3], [0x0F, 0xC8], [0x0F, 0xCB],
            [0x0F, 0xA4, 0xC3, 0x04], [0x0F, 0xA5, 0xC3], [0x0F, 0xAC, 0xC3, 0x04], [0x0F, 0xAD, Mem(0)],
            // 文字列
            [0xA4], [0xA5], [0x66, 0xA5], [0xAA], [0xAB], [0xAC], [0xAD], [0xA6], [0xA7], [0xAE], [0xAF], [0x6C], [0x6D], [0x6E], [0x6F],
            [0xF3, 0xA4], [0xF3, 0xAB], [0x66, 0xF3, 0xAB], [0xF3, 0xA6], [0xF2, 0xAE], [0xF3, 0x6D], [0xF3, 0x6F], [0xF3, 0xA5], [0xF2, 0xA7], [0xF3, 0x90], [0xF3, 0xC3], [0xF3, 0x0F, 0xBC, 0xC3],
            // ALU
            [0x00, 0xC3], [0x01, Mem(0)], [0x02, Mem(0)], [0x03, 0xC3], [0x04, 0xFF], [0x05, 0x01, 0x00, 0x00, 0x80], [0x66, 0x05, 0x01, 0x80], [0x3B, Mem(0)], [0x29, 0xC3], [0x13, 0xC3], [0x1B, 0xC3], [0x31, 0xC3], [0x21, 0xC3], [0x09, 0xC3],
            // 残り
            [0x98], [0x66, 0x98], [0x99], [0x66, 0x99], [0x9E], [0x9F], [0xD7], [0xF5], [0xF8], [0xF9], [0xFC], [0xFD], [0x90], [0xC9], [0xC8, 0x10, 0x00, 0x00], [0xC8, 0x10, 0x00, 0x02],
            [0x27], [0x2F], [0x37], [0x3F], [0xD4, 0x0A], [0xD5, 0x0A], [0xCC], [0xCD, 0x21], [0xCF], [0x66, 0xCF], [0xF4],
            // CMPXCHG8B: 不一致([bx])、一致([bx+0x60] には EDX:EAX と同じ値を置いてある)、不正な形(レジスタ形式、/0)
            [0x0F, 0xC7, Mem(1)], [0x0F, 0xC7, .. Mem8(1, 0x60)], [0x0F, 0xC7, 0xC8], [0x0F, 0xC7, Mem(0)],
            // 割り込みフラグ、CR の読み書き(無い CR とメモリ形式を含む)
            [0xFA], [0xFB], [0x0F, 0x20, 0xC8], [0x0F, 0x22, 0xC8], [0x0F, 0x20, 0x00], [0x0F, 0x22, 0xC0], [0x0F, 0x22, 0xE0], [0x0F, 0x20, 0xE0], [0x0F, 0x20, 0xD0],
            // 記述子テーブル(SGDT/SIDT/LGDT/LIDT。レジスタ形式は不正だが動作を固定する)
            [0x0F, 0x01, Mem(0)], [0x0F, 0x01, Mem(1)], [0x0F, 0x01, Mem(2)], [0x0F, 0x01, Mem(3)], [0x66, 0x0F, 0x01, Mem(0)], [0x66, 0x0F, 0x01, Mem(2)],
            [0x0F, 0x01, 0xC3], [0x0F, 0x01, 0xCB], [0x0F, 0x01, 0xD3], [0x0F, 0x01, 0xDB], [0x0F, 0x01, 0xC6], [0x0F, 0x01, 0xD6],
            // SLDT/STR/LLDT/LTR、VERR/VERW、LAR/LSL。[bx+2] のワードは 0x0008(pm32 ではコードセグメントのセレクタ)
            [0x0F, 0x00, 0xC3], [0x0F, 0x00, 0xCB], [0x0F, 0x00, 0xD3], [0x0F, 0x00, 0xDB], [0x0F, 0x00, 0xE3], [0x0F, 0x00, 0xEB], [0x0F, 0x00, 0xF3],
            [0x0F, 0x00, Mem(0)], [0x0F, 0x00, Mem(1)], [0x0F, 0x00, Mem(2)], [0x0F, 0x00, Mem(3)], [0x0F, 0x00, Mem(4)], [0x0F, 0x00, Mem(5)],
            [0x0F, 0x00, .. Mem8(2, 2)], [0x0F, 0x00, .. Mem8(3, 2)], [0x0F, 0x00, .. Mem8(4, 2)], [0x0F, 0x00, .. Mem8(5, 2)], [0x66, 0x0F, 0x00, 0xC3], [0x66, 0x0F, 0x00, 0xCB],
            [0x0F, 0x02, 0xC3], [0x0F, 0x03, 0xC3], [0x0F, 0x02, .. Mem8(0, 2)], [0x0F, 0x03, .. Mem8(0, 2)], [0x66, 0x0F, 0x02, .. Mem8(0, 2)], [0x66, 0x0F, 0x03, .. Mem8(0, 2)],
            // マシンステータスワード: CLTS、SMSW(レジスタ形式、16 ビット指定、メモリ形式)、LMSW(レジスタ形式、メモリ形式)
            [0x0F, 0x06], [0x0F, 0x01, 0xE3], [0x66, 0x0F, 0x01, 0xE3], [0x0F, 0x01, Mem(4)], [0x0F, 0x01, 0xF3], [0x0F, 0x01, Mem(6)],
            // ENTER: 入れ子なし、3 段、16 ビット指定(フレームポインタの幅はオペランドサイズ)
            [0xC8, 0x08, 0x00, 0x00], [0xC8, 0x10, 0x00, 0x03], [0x66, 0xC8, 0x08, 0x00, 0x00], [0x66, 0xC8, 0x04, 0x00, 0x02],
            // 記述子の照合: Selectors の各セレクタ([bx+0x200+2k] に並べてある)を VERR、VERW、LAR、LSL に通す
            .. (from slot in Enumerable.Range(0, Selectors.Length)
                let disp = 0x200u + 2 * (uint)slot
                from code in new byte[][] { [0x0F, 0x00, .. MemDisp(4, disp)], [0x0F, 0x00, .. MemDisp(5, disp)], [0x0F, 0x02, .. MemDisp(0, disp)], [0x0F, 0x03, .. MemDisp(0, disp)] }
                select code),
            // トラップゲート(型 0xF)経由の INT: IF を保つ
            [0xCD, 0x22],
            // FPU: 環境と全体の保存・復元、制御/状態ワード、初期化
            [0xD9, Mem(4)], [0xD9, Mem(5)], [0xD9, Mem(6)], [0xD9, Mem(7)], [0xDD, Mem(4)], [0xDD, Mem(6)], [0xDD, Mem(7)], [0xDF, 0xE0], [0xDB, 0xE2], [0xDB, 0xE3], [0x9B],
            // FPU: メモリ形式のロード/ストアと算術(D8 = f32、DA = int32、DC = f64、DE = int16)
            [0xD9, Mem(0)], [0xD9, Mem(2)], [0xD9, Mem(3)], [0xDB, Mem(0)], [0xDB, Mem(2)], [0xDB, Mem(3)], [0xDB, Mem(5)], [0xDB, Mem(7)],
            [0xDD, Mem(0)], [0xDD, Mem(2)], [0xDD, Mem(3)], [0xDF, Mem(0)], [0xDF, Mem(2)], [0xDF, Mem(3)], [0xDF, Mem(5)], [0xDF, Mem(7)],
            .. (from op in new byte[] { 0xD8, 0xDA, 0xDC, 0xDE } from kind in Enumerable.Range(0, 8) select new byte[] { op, Mem(kind) }),
            // FPU: レジスタ形式(算術グループは ST(1) と。DE D9 = FCOMPP)
            .. (from op in new byte[] { 0xD8, 0xDC, 0xDE } from kind in Enumerable.Range(0, 8) select new byte[] { op, (byte)(0xC1 | (kind << 3)) }),
            [0xD9, 0xC1], [0xD9, 0xC9], [0xD9, 0xD0], [0xD9, 0xD9], [0xD9, 0xE0], [0xD9, 0xE1], [0xD9, 0xE4], [0xD9, 0xE5],
            [0xD9, 0xE8], [0xD9, 0xE9], [0xD9, 0xEA], [0xD9, 0xEB], [0xD9, 0xEC], [0xD9, 0xED], [0xD9, 0xEE],
            [0xD9, 0xF0], [0xD9, 0xF1], [0xD9, 0xF2], [0xD9, 0xF3], [0xD9, 0xF4], [0xD9, 0xF5], [0xD9, 0xF6], [0xD9, 0xF7],
            [0xD9, 0xF8], [0xD9, 0xF9], [0xD9, 0xFA], [0xD9, 0xFB], [0xD9, 0xFC], [0xD9, 0xFD], [0xD9, 0xFE], [0xD9, 0xFF],
            [0xDA, 0xC1], [0xDA, 0xC9], [0xDA, 0xD1], [0xDA, 0xD9], [0xDA, 0xE9], [0xDB, 0xC1], [0xDB, 0xC9], [0xDB, 0xD1], [0xDB, 0xD9], [0xDB, 0xE8], [0xDB, 0xE9], [0xDB, 0xF1],
            [0xDD, 0xC1], [0xDD, 0xD1], [0xDD, 0xD9], [0xDD, 0xE1], [0xDD, 0xE9], [0xDF, 0xE9], [0xDF, 0xF1],
        ];
        return list;
    }

    // 記述子の照合(VERR/VERW/LAR/LSL)に通すセレクタ。GDT(Run が置く 16 項)の各種の記述子を、RPL を変えて指す。
    //   00 ヌル、08 コード、10 データ、18 コード DPL3、20 データ DPL3、28 16 ビットコード、30 コンフォーミング、38 実行専用、40 読み取り専用データ、
    //   48 TSS、50 LDT、58 コールゲート DPL3、60 タスクゲート、68 割り込みゲート、70 不在のデータ、78 コンフォーミング DPL3(バイト単位のリミット)。
    //   80 と FFF8 は GDT の外、0C は LDT を指すセレクタ(TI=1)。
    static readonly ushort[] Selectors =
    [
        0x0000, 0x0003, 0x0008, 0x000B, 0x0010, 0x0013, 0x0018, 0x001B, 0x0023, 0x0028, 0x0030, 0x0033, 0x0038, 0x0040,
        0x0048, 0x0050, 0x0058, 0x005B, 0x0060, 0x0068, 0x0070, 0x0078, 0x007B, 0x0080, 0x000C, 0xFFF8,
    ];

    // 特権レベルと記述子の内容で分かれる経路。pm32 の状態(preset 0)から、場面ごとに CPL・スタックの枠・GDT の項を置き換えて 1 命令実行する。
    //   iret : 32 ビット IRET の復帰先 CS の検査(ヌル、データ、システム記述子、DPL と RPL の組、コンフォーミング、CPL 3 から、V86 への復帰)
    //   cpl3 / v86 : リング 3 と V86 モードからの INT(TSS の SS0:ESP0 へのスタック切り替え、ゲートの DPL と IOPL の検査)と IRET、フラグ操作。
    //                リング 3 からは記述子の照合(VERR/VERW/LAR/LSL)も
    //   load : 基底が 0 でない記述子と D/B ビットが落ちた記述子を、DS・SS・FS・CS へロードする
    static IEnumerable<string> Scenes()
    {
        static void Dwords(EmuEnvironment env, int address, params uint[] values)
        {
            for (var i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(env.Ram, address + i * 4);
        }
        // TR = 0x48(GDT の TSS の基底は 0x7000。ESP0 = 0x9000、SS0 = 0x10)
        static void Tss(EmuEnvironment env, CPU cpu)
        {
            cpu.tr = 0x48;
            Dwords(env, 0x7000, 0, 0x00009000, 0x00000010);
        }
        static void Ring3(EmuEnvironment env, CPU cpu)
        {
            Tss(env, cpu);
            CPU._cs.setter(cpu)(0x1B); cpu.cs_base = 0;
            CPU._ss.setter(cpu)(0x23); cpu.ss_base = 0;
        }
        // V86 モード: セグメントは セレクタ*16(CS = SS = DS = ES = 0、FS = 0x100、GS = 0x200)、コードもスタックも 16 ビット。
        static void V86(EmuEnvironment env, CPU cpu, uint iopl)
        {
            Tss(env, cpu);
            cpu.eflags = 0x00020202 | (iopl << 12);
            foreach (var segment in new[] { CPU._cs, CPU._ss, CPU._ds, CPU._es }) segment.setter(cpu)(0);
            CPU._fs.setter(cpu)(0x0100);
            CPU._gs.setter(cpu)(0x0200);
            cpu.code32 = false; cpu.stack32 = false;
        }

        // IRET の枠: EIP、CS、EFLAGS、ESP、SS、(V86 への復帰で)ES、DS、FS、GS。
        var iret =
            from target in new (ushort cs, uint flags, bool ring3)[]
            {
                (0x0000, 0x0202, false), (0x0003, 0x0202, false), (0x0008, 0x0202, false), (0x000B, 0x0202, false), (0x0010, 0x0202, false),
                (0x0018, 0x0202, false), (0x001B, 0x0202, false), (0x0030, 0x0202, false), (0x0033, 0x0202, false), (0x0038, 0x0202, false),
                (0x0048, 0x0202, false), (0x0068, 0x0202, false), (0x0070, 0x0202, false), (0x0078, 0x0202, false), (0x007B, 0x0202, false),
                (0x0000, 0x00023202, false), (0x0008, 0x00023202, false),
                (0x0008, 0x0202, true), (0x001B, 0x0202, true), (0x0033, 0x0202, true), (0x007B, 0x0202, true), (0x0000, 0x00023202, true), (0x001B, 0x00023202, true),
            }
            select Run("pm32", 0, [0xCF], $"iret cs={target.cs:x4} fl={target.flags:x} {(target.ring3 ? "cpl3" : "cpl0")}", (env, cpu) =>
            {
                Dwords(env, 0x8000, 0x00001234, target.cs, target.flags, 0x00007000, 0x00000023, 0x0100, 0x0200, 0x0300, 0x0400);
                if (target.ring3) Ring3(env, cpu);
            });

        var interrupts =
            from scene in new (string name, Action<EmuEnvironment, CPU> setup)[]
            {
                ("cpl3", Ring3),
                ("v86 iopl3", (env, cpu) => V86(env, cpu, 3)),
                ("v86 iopl0", (env, cpu) => V86(env, cpu, 0)),
            }
            from code in new byte[][] { [0xCD, 0x21], [0xCD, 0x22], [0xCD, 0x23], [0xCC], [0xCF], [0x9C], [0x9D], [0xFA], [0xFB] }
            select Run("pm32", 0, code, scene.name, scene.setup);

        var loads =
            from descriptor in new (string name, uint low, uint high)[]
            {
                ("load base", 0x5678FFFF, 0x12CF9234),  // 基底 0x12345678、32 ビット
                ("load 16bit", 0x3000FFFF, 0x00009200), // 基底 0x00003000、D/B = 0
            }
            from code in new byte[][] { [0x8E, 0xD8], [0x8E, 0xD0], [0x8E, 0xE0], [0xEA, 0x00, 0x10, 0x00, 0x00, 0x40, 0x00], [0x9A, 0x00, 0x10, 0x00, 0x00, 0x40, 0x00] }
            select Run("pm32", 0, code, descriptor.name, (env, cpu) =>
            {
                Dwords(env, 0x5000 + 0x40, descriptor.low, descriptor.high);
                cpu.eax = 0x40;
            });

        // 記述子の照合を CPL 3 から(DPL >= CPL の検査が効く): Selectors の各セレクタを VERR、VERW、LAR、LSL に通す。
        var checks =
            from slot in Enumerable.Range(0, Selectors.Length)
            let disp = 0x200 + 2 * slot
            from code in new byte[][] { [0x0F, 0x00, 0xA3], [0x0F, 0x00, 0xAB], [0x0F, 0x02, 0x83], [0x0F, 0x03, 0x83] } // [ebx+disp32]
            select Run("pm32", 0, [.. code, (byte)disp, (byte)(disp >> 8), 0, 0], "cpl3", Ring3);

        return iret.Concat(interrupts).Concat(loads).Concat(checks);
    }

    // 1 ケース: 64KB の環境に命令列・データ・スタック・GDT・IVT/IDT を置き、決まったレジスタで 1 命令実行して結果を 1 行にする。
    // scene があれば、実行の直前に setup で状態を置き換える(行の先頭に scene の名前が入る)。
    static string Run(string mode, int preset, byte[] code, string scene = null, Action<EmuEnvironment, CPU> setup = null)
    {
        var env = new EmuEnvironment(memory: new byte[0x10000]);
        var ram = env.Ram;
        code.CopyTo(ram, 0);
        // データ領域 0x3000-0x33FF: 決まったパターン。far ポインタ [0x3000] = 0x1000:0008 / 0x00001000:0008。
        for (var i = 0; i < 0x400; i++) ram[0x3000 + i] = (byte)(i * 7 + 3 + preset);
        for (var i = 0; i < 0x400; i++) ram[0x4000 + i] = (byte)(i * 5 + 1 - preset);
        BitConverter.GetBytes(0x00001000u).CopyTo(ram, 0x3000);
        BitConverter.GetBytes((ushort)0x0008).CopyTo(ram, 0x3004);
        BitConverter.GetBytes((ushort)0x0008).CopyTo(ram, 0x3002); // 16 ビット far ポインタの selector
        // 0x3200-: 記述子の照合に使うセレクタの表。
        for (var i = 0; i < Selectors.Length; i++) BitConverter.GetBytes(Selectors[i]).CopyTo(ram, 0x3200 + 2 * i);
        // スタック 0x8000-: 戻り番地・CS(RPL 3)・外側スタックの ESP/SS、以降はパターン。
        for (var i = 0; i < 0x100; i++) ram[0x8000 + i] = (byte)(i * 3 + 7);
        if (preset == 0)
        {
            BitConverter.GetBytes(0x00001234u).CopyTo(ram, 0x8000);
            BitConverter.GetBytes(0x0000001Bu).CopyTo(ram, 0x8004);
            BitConverter.GetBytes(0x00007000u).CopyTo(ram, 0x8008);
            BitConverter.GetBytes(0x00000023u).CopyTo(ram, 0x800C);
            BitConverter.GetBytes(0x00000202u).CopyTo(ram, 0x8010);
        }
        else
        {
            BitConverter.GetBytes((ushort)0x1234).CopyTo(ram, 0x8000);
            BitConverter.GetBytes((ushort)0x0008).CopyTo(ram, 0x8002);
            BitConverter.GetBytes((ushort)0x0202).CopyTo(ram, 0x8004);
        }
        // GDT at 0x5000(16 項、下位 dword・上位 dword の順): 08=code32, 10=data32, 18=code32 DPL3, 20=data32 DPL3, 28=code16、
        // 30 以降は記述子の照合用(Selectors の説明を参照)。
        uint[] gdt =
        [
            0, 0, 0x0000FFFF, 0x00CF9A00, 0x0000FFFF, 0x00CF9200, 0x0000FFFF, 0x00CFFA00, 0x0000FFFF, 0x00CFF200, 0x0000FFFF, 0x008F9A00,
            0x0000FFFF, 0x00CF9E00, 0x0000FFFF, 0x00CF9800, 0x0000FFFF, 0x00CF9000, 0x70000067, 0x00008900, 0x00000FFF, 0x00008200,
            0x00081234, 0x0000EC00, 0x00480000, 0x00008500, 0x00080300, 0x00008E00, 0x0000FFFF, 0x00CF1200, 0x00002345, 0x0041FE00,
        ];
        for (var i = 0; i < gdt.Length; i++) BitConverter.GetBytes(gdt[i]).CopyTo(ram, 0x5000 + i * 4);
        // IVT: ベクタ 3/0x21 を 0x0100:0x0200 へ、0x22 を 0x0100:0x0300 へ。
        BitConverter.GetBytes(0x01000200u).CopyTo(ram, 3 * 4);
        BitConverter.GetBytes(0x01000200u).CopyTo(ram, 0x21 * 4);
        BitConverter.GetBytes(0x01000300u).CopyTo(ram, 0x22 * 4);
        for (var i = 0; i < 8; i++) { env.IoPort[0x10 + i] = (byte)(0xA0 + i); env.IoPort[0x200 + i] = (byte)(0xB0 + i); }
        env.Tsc = 0x1122334455667788;
        env.Msrs[0x10] = 0x0123456789ABCDEF;
        env.Dr[0] = 0xDEADBEEF; env.Dr[7] = 0x00000400;

        var cpu = new CPU();
        var negative = preset == 1;
        cpu.eax = negative ? 0xFFFFFF80u : 0x12345678u;
        cpu.ebx = 0x3000;
        cpu.ecx = negative ? 0x00000000u : 0x00000003u;
        cpu.edx = 0x0200;
        cpu.esi = 0x3010;
        cpu.edi = 0x4000;
        cpu.ebp = 0x7F00;
        cpu.esp = 0x8000;
        cpu.eflags = negative ? 0x0A47u : 0x0202u; // preset 1: CF/ZF/SF/OF/PF
        // [0x3060] に EDX:EAX と同じ 64 ビット値(CMPXCHG8B の一致側)。
        BitConverter.GetBytes(cpu.eax).CopyTo(ram, 0x3060);
        BitConverter.GetBytes(cpu.edx).CopyTo(ram, 0x3064);
        // FPU: preset 0 は ST0-ST2 だけ有効。preset 1 は 8 本とも有効(0、NaN、無限大、非正規化数を含む)で、丸めは切り捨て、C3/C0 が立っている。
        if (negative)
        {
            cpu.fpu_cw = 0x0F7F; cpu.fpu_sw = 0x4100; cpu.fpu_top = 0; cpu.fpu_valid = 0xFF;
            new[] { 0.0, -2.5, double.NaN, double.PositiveInfinity, 3.0, -7.5, 1e-310, 123456789.125 }.CopyTo(cpu.fpu_st, 0);
        }
        else
        {
            cpu.fpu_top = 5; cpu.fpu_valid = 0xE0;
            cpu.fpu_st[5] = 1.5; cpu.fpu_st[6] = -2.25; cpu.fpu_st[7] = 1e10;
        }
        if (mode == "pm32")
        {
            CPU._cr0.setter(cpu)(1);
            cpu.gdt_base = 0x5000; cpu.gdt_limit = 0x7F;
            cpu.idt_base = 0x6000; cpu.idt_limit = 0x7FF;
            // IDT: ベクタ 3/0x21 を 0008:00000300 の割り込みゲートへ、0x22 を 0008:00000400 のトラップゲートへ、
            // 0x23 を 0008:12340500 の割り込みゲート(DPL 3: V86 モードの INT n が通る)へ。
            foreach (var vector in new[] { 3, 0x21 })
            {
                BitConverter.GetBytes(0x00080300u).CopyTo(ram, 0x6000 + vector * 8);
                BitConverter.GetBytes(0x00008E00u).CopyTo(ram, 0x6000 + vector * 8 + 4);
            }
            BitConverter.GetBytes(0x00080400u).CopyTo(ram, 0x6000 + 0x22 * 8);
            BitConverter.GetBytes(0x00008F00u).CopyTo(ram, 0x6000 + 0x22 * 8 + 4);
            BitConverter.GetBytes(0x00080500u).CopyTo(ram, 0x6000 + 0x23 * 8);
            BitConverter.GetBytes(0x1234EE00u).CopyTo(ram, 0x6000 + 0x23 * 8 + 4);
            CPU._cs.setter(cpu)(0x08); cpu.cs_base = 0; cpu.code32 = true;
            CPU._ss.setter(cpu)(0x10); cpu.ss_base = 0; cpu.stack32 = true;
            CPU._ds.setter(cpu)(0x10); cpu.ds_base = 0;
            CPU._es.setter(cpu)(0x10); cpu.es_base = 0;
            CPU._fs.setter(cpu)(0x20); cpu.fs_base = 0;
            CPU._gs.setter(cpu)(0x20); cpu.gs_base = 0;
        }
        else
        {
            CPU._fs.setter(cpu)(0x0100);
            CPU._gs.setter(cpu)(0x0200);
        }
        cpu.eip = 0;
        setup?.Invoke(env, cpu);
        env.Paging.Sync(cpu);

        string result;
        try
        {
            var (ok, _, next, _) = Step(env, cpu, null);
            result = ok ? "ok" : "fail";
            cpu = next;
        }
        catch (Exception exception)
        {
            result = $"{exception.GetType().Name}:{exception.Message}";
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        cpu.WriteTo(writer);
        cpu.SaveRuntimeState(writer); // FPU スタック、TR、LDTR(WriteTo に無い分)
        var state = Convert.ToHexString(stream.ToArray());
        var memory = Convert.ToHexString(SHA256.HashData(ram))[..16];
        var ports = Convert.ToHexString(SHA256.HashData(env.IoPort))[..16];
        var msrs = string.Join(",", env.Msrs.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key:x}={pair.Value:x}"));
        var dr = string.Join(",", env.Dr.Select(value => value.ToString("x")));
        return $"{mode} p{preset} {(scene == null ? "" : $"[{scene}] ")}{Convert.ToHexString(code)}: {result} cpu={state} mem={memory} io={ports} msr={msrs} dr={dr}";
    }

    static byte[] Saved(Action<BinaryWriter> save)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        save(writer);
        writer.Flush();
        return stream.ToArray();
    }

    static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data))[..16];

    // 命令の実行ではない結果を 1 項 1 行で: VGA BIOS の ROM、PCI コンフィグ空間の初期値、
    // モード設定後の BIOS データ領域(0x400-0x4FF)と VGA の状態、ATA の IDENTIFY の応答、
    // INT 10h のサービスを決まった手順で呼んだ結果、PCI コンフィグ空間への読み書きの結果。
    static List<string> Tables()
    {
        var lines = new List<string> { $"table rom: {Convert.ToHexString(VgaBiosStub.Rom)}" };
        using (var env = new EmuEnvironment(memory: new byte[0x100000]))
            lines.Add($"table pci: {Convert.ToHexString(Saved(env.Pci.SaveState))}");
        foreach (var mode in new[] { 0x03, 0x12, 0x13, 0x83, 0x0D, 0x6A, 0x01 })
        {
            using var env = new EmuEnvironment(memory: new byte[0x100000]);
            for (var i = 0; i < 0x100; i++) env.Ram[0x400 + i] = (byte)(i * 3 + 1); // 書かない番地が変わらないことも見る
            VgaBiosStub.SetMode(env, mode);
            var vga = Convert.ToHexString(SHA256.HashData(Saved(env.Vga.SaveState)));
            lines.Add($"table mode {mode:x2}: bda={Convert.ToHexString(env.Ram, 0x400, 0x100)} vga={vga}");
        }
        // IDENTIFY: 4MB の生イメージをつないだ ATA デバイスにコマンドを出し、応答の 512 バイトを読む。
        var image = Path.Combine(Path.GetTempPath(), $"emu86-diff-{Guid.NewGuid():N}.img");
        File.WriteAllBytes(image, new byte[4 << 20]);
        try
        {
            using var ata = new AtaDevice(new DiskImage(image));
            ata.WriteReg(0x1F7, 0xEC);
            lines.Add($"table identify: {Convert.ToHexString([.. Enumerable.Range(0, 512).Select(_ => (byte)ata.ReadData(1))])}");
        }
        finally { File.Delete(image); }
        lines.AddRange(Int10());
        lines.AddRange(PciAccess());
        return lines;
    }

    // VGA BIOS スタブの導入と、INT 10h のサービス(VgaBiosStub.Service)。決まった手順で呼び、1 手順ごとに
    // AX-DX、BIOS データ領域のビデオ項目(0x449-0x48A)、VGA の状態(VRAM・CRTC・Attribute・DAC のハッシュ)を 1 行にする。
    static IEnumerable<string> Int10()
    {
        static Action<EmuEnvironment, CPU> Call(ushort ax, ushort bx = 0, ushort cx = 0, ushort dx = 0) => (env, cpu) =>
        {
            cpu.eax = ax; cpu.ebx = bx; cpu.ecx = cx; cpu.edx = dx;
            VgaBiosStub.Service(env, cpu);
        };
        static string Screen(EmuEnvironment env) =>
            $"bda={Convert.ToHexString(env.Ram, 0x449, 0x42)} vga={Hash([.. env.Vga.Vram, .. env.Vga.Crtc, .. env.Vga.Attribute, .. env.Vga.Dac])}";

        using var env = new EmuEnvironment(memory: new byte[0x100000]);
        var cpu = new CPU();
        VgaBiosStub.Install(env);
        yield return $"table install: installed={VgaBiosStub.IsInstalled(env)} ivt={Convert.ToHexString(env.Ram, 0x40, 4)} rom={Hash(env.Ram.AsSpan(0xC0000, 0x800))} {Screen(env)}";

        (string name, Action<EmuEnvironment, CPU> run)[] steps =
        [
            ("set cursor 5,10", Call(0x0200, dx: 0x050A)),
            ("get cursor", Call(0x0300)),
            ("write char and attribute x5", Call(0x0941, bx: 0x001E, cx: 5)),
            ("write char x3", Call(0x0A42, cx: 3)),
            ("read cell", Call(0x0800)),
            ("teletype X", Call(0x0E58)),
            ("teletype CR", Call(0x0E0D)),
            ("teletype BS at column 0", Call(0x0E08)),
            ("teletype LF", Call(0x0E0A)),
            ("teletype BEL", Call(0x0E07)),
            ("teletype Q", Call(0x0E51)),
            ("teletype BS", Call(0x0E08)),
            ("set cursor 3,79", Call(0x0200, dx: 0x034F)),
            ("teletype wraps to the next row", Call(0x0E59)),
            ("set cursor 24,79", Call(0x0200, dx: 0x184F)),
            ("teletype scrolls at the last cell", Call(0x0E5A)),
            ("teletype LF scrolls on the last row", Call(0x0E0A)),
            ("write char beyond the page", Call(0x0941, bx: 0x0007, cx: 0xFFFF)),
            ("scroll up 1, full screen", Call(0x0601, bx: 0x1700, cx: 0x0000, dx: 0x184F)),
            ("scroll up 3, window", Call(0x0603, bx: 0x2A00, cx: 0x0205, dx: 0x0A28)),
            ("scroll down 2, window", Call(0x0702, bx: 0x4E00, cx: 0x0205, dx: 0x0A28)),
            ("scroll up, clear window", Call(0x0600, bx: 0x0700, cx: 0x0205, dx: 0x0A28)),
            ("scroll down 1, window beyond the screen", Call(0x0701, bx: 0x3000, cx: 0x0000, dx: 0x1E64)),
            ("scroll up, more lines than the window", Call(0x0620, bx: 0x5000, cx: 0x0500, dx: 0x0A10)),
            ("scroll up, empty window", Call(0x0601, bx: 0x0700, cx: 0x0A00, dx: 0x0500)),
            ("cursor shape", Call(0x0100, cx: 0x0607)),
            ("get cursor after shape", Call(0x0300)),
            ("get mode", Call(0x0F00, bx: 0xFFFF)),
            ("palette register", Call(0x1000, bx: 0x3F01)),
            ("palette register out of range", Call(0x1000, bx: 0x3FFF)),
            ("dac register", Call(0x1010, bx: 0x0105, cx: 0xC283, dx: 0x4100)),
            ("palette, unknown function", Call(0x1055, bx: 0x0101)),
            ("ega information", Call(0x1200, bx: 0xFF10, cx: 0xFFFF)),
            .. Enumerable.Range(0x30, 8).Select(function => ($"video subsystem {function:x2}", Call(0x1200, bx: (ushort)function))),
            ("display combination", Call(0x1A00, bx: 0xFFFF)),
            ("display combination, set", Call(0x1A01, bx: 0x0102)),
            ("vesa", Call(0x4F00)),
            ("unknown service", Call(0x5512, bx: 0x3456, cx: 0x789A, dx: 0xBCDE)),
            ("set mode 13", Call(0x0013)),
            ("get mode in 13", Call(0x0F00)),
            ("teletype in graphics", Call(0x0E41)),
            ("write char in graphics", Call(0x0941, bx: 0x000F, cx: 2)),
            ("read cell in graphics", Call(0x0800)),
            ("scroll in graphics", Call(0x0601, bx: 0x0700, dx: 0x184F)),
            ("set cursor in graphics", Call(0x0200, dx: 0x0203)),
            ("set mode 12", Call(0x0012)),
            ("get mode in 12", Call(0x0F00)),
            ("set mode 3 without clearing", Call(0x0083)),
            ("get mode in 83", Call(0x0F00)),
            ("set cursor with the columns field 0", (env, cpu) => { env.Ram[0x44A] = 0; Call(0x0200, dx: 0x0102)(env, cpu); }),
            ("teletype with the columns field 0", Call(0x0E52)),
            ("set mode 3", Call(0x0003)),
            ("teletype after mode 3", Call(0x0E53)),
        ];
        var index = 0;
        foreach (var (name, run) in steps)
        {
            run(env, cpu);
            yield return $"table int10 {index++:d2} {name}: ax={cpu.ax:x4} bx={cpu.bx:x4} cx={cpu.cx:x4} dx={cpu.dx:x4} {Screen(env)}";
        }
    }

    // PCI コンフィグ空間への読み書き(ポート 0xCF8/0xCFC の先の PciHost.DataRead/DataWrite)。各ファンクションと、存在しないデバイス・
    // enable の落ちたアドレス・bus 1 について、主なレジスタに幅(1/2/4 バイト)とバイトずれを変えて
    // 「読む → 全ビット 1 を書く → 読む → パターンを書く → 読む」を行い、読んだ値の列のハッシュを 1 行にする。最後に全コンフィグ空間を出す。
    static IEnumerable<string> PciAccess()
    {
        using var env = new EmuEnvironment(memory: new byte[0x100000]);
        var pci = env.Pci;
        int[] registers = [0x00, 0x04, 0x08, 0x0C, 0x10, 0x14, 0x20, 0x24, 0x30, 0x3C, 0x40, 0x58, 0x80, 0xD0, 0xFC];
        (int offset, int size)[] accesses = [(0, 4), (0, 2), (2, 2), (0, 1), (1, 1), (2, 1), (3, 1), (3, 2), (1, 4)];
        (int bus, int device, int function, bool enabled)[] targets =
            [(0, 0, 0, true), (0, 1, 0, true), (0, 1, 1, true), (0, 1, 3, true), (0, 2, 0, true), (0, 3, 0, true), (0, 1, 1, false), (1, 1, 1, true)];
        foreach (var (bus, device, function, enabled) in targets)
        {
            var reads = new List<uint>();
            foreach (var register in registers)
                foreach (var (offset, size) in accesses)
                {
                    pci.Address = (enabled ? 0x80000000u : 0) | ((uint)bus << 16) | ((uint)device << 11) | ((uint)function << 8) | (uint)register;
                    reads.Add(pci.DataRead(offset, size));
                    pci.DataWrite(offset, size, 0xFFFFFFFF);
                    reads.Add(pci.DataRead(offset, size));
                    pci.DataWrite(offset, size, 0x12345678 + (uint)register);
                    reads.Add(pci.DataRead(offset, size));
                }
            var values = string.Join(",", reads.Select(value => value.ToString("x")));
            yield return $"table pci access {bus:x2}:{device:x2}.{function}{(enabled ? "" : " disabled")}: reads={Hash(System.Text.Encoding.ASCII.GetBytes(values))}"
                + $" lfb={pci.LfbBase:x8} bm={pci.IdeBmBase:x}";
        }
        // VGA の拡張 ROM BAR を有効にして、ROM 窓(64KB)の内と外を見る。
        pci.Address = 0x80000000 | (2u << 11) | 0x30;
        var before = pci.RomContains(0xFEBC0010);
        pci.DataWrite(0, 4, 0xFEBC0001);
        yield return $"table pci rom: bar={pci.DataRead(0, 4):x8} contains={before},{pci.RomContains(0xFEBC0010)},{pci.RomContains(0xFEBCFFFF)},{pci.RomContains(0xFEBD0000)},{pci.RomContains(0xFEBBFFFF)}"
            + $" read={pci.RomRead(0xFEBC0002):x2},{pci.RomRead(0xFEBC0100):x2},{pci.RomRead(0xFEBC0900):x2}";
        yield return $"table pci after access: {Convert.ToHexString(Saved(pci.SaveState))}";
    }
}
