using System.Text;

namespace Emu86;

// 中断中・停止後の CPU レジスタを表示するモードレスダイアログ。閉じても破棄せず隠すだけで、
// 次に表示するときに最新の状態で更新する。
internal sealed class RegisterDialog : Form
{
    readonly TextBox text = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10f),
        BackColor = SystemColors.Window,
        WordWrap = false,
    };

    public RegisterDialog()
    {
        Text = "CPU レジスタ";
        ClientSize = new Size(800, 360);
        MinimumSize = new Size(400, 200);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(text);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    public void Update(CPU cpu, long count, string state)
    {
        text.Text = Format(cpu, count, state);
        text.Select(0, 0);
    }

    public static string Format(CPU cpu, long count, string state)
    {
        var builder = new StringBuilder();
        var cr0 = CPU._cr0.getter(cpu);
        var mode = !cpu.pe ? "real" : cpu.vm ? "virtual-8086" : $"protected ring{cpu.cpl} {(cpu.code32 ? "32" : "16")}-bit";
        builder.AppendLine($"{state}  instructions={count:N0}");
        builder.AppendLine($"mode={mode}  paging={(cpu.pg ? "on" : "off")}  stack={(cpu.stack32 ? "32" : "16")}-bit");
        builder.AppendLine();
        builder.AppendLine($"EAX={cpu.eax:X8}  EBX={cpu.ebx:X8}  ECX={cpu.ecx:X8}  EDX={cpu.edx:X8}");
        builder.AppendLine($"ESI={cpu.esi:X8}  EDI={cpu.edi:X8}  EBP={cpu.ebp:X8}  ESP={cpu.esp:X8}");
        builder.AppendLine($"EIP={cpu.eip:X8}  EFLAGS={cpu.eflags:X8}  [{Flags(cpu.eflags)}]");
        builder.AppendLine();
        builder.AppendLine($"CS={cpu.cs:X4} base={cpu.cs_base:X8}   DS={cpu.ds:X4} base={cpu.ds_base:X8}   ES={cpu.es:X4} base={cpu.es_base:X8}");
        builder.AppendLine($"SS={cpu.ss:X4} base={cpu.ss_base:X8}   FS={cpu.fs:X4} base={cpu.fs_base:X8}   GS={cpu.gs:X4} base={cpu.gs_base:X8}");
        builder.AppendLine();
        builder.AppendLine($"CR0={cr0:X8}  CR2={cpu.cr2:X8}  CR3={cpu.cr3:X8}  CR4={cpu.cr4:X8}");
        builder.AppendLine($"GDTR={cpu.gdt_base:X8}/{cpu.gdt_limit:X4}  IDTR={cpu.idt_base:X8}/{cpu.idt_limit:X4}  TR={cpu.tr:X4}  LDTR={cpu.ldtr:X4}");
        builder.AppendLine();
        builder.AppendLine($"FPU CW={cpu.fpu_cw:X4} SW={cpu.fpu_sw:X4} TOP={cpu.fpu_top}");
        for (var index = 0; index < 8; index++)
        {
            var physical = (cpu.fpu_top + index) & 7;
            var valid = (cpu.fpu_valid & (1 << physical)) != 0;
            builder.AppendLine($"ST({index})={(valid ? cpu.fpu_st[physical].ToString("R") : "empty")}");
        }
        return builder.ToString();
    }

    static string Flags(uint eflags)
    {
        (int bit, string name)[] bits =
        [
            (0, "CF"), (2, "PF"), (4, "AF"), (6, "ZF"), (7, "SF"), (8, "TF"), (9, "IF"), (10, "DF"), (11, "OF"),
            (14, "NT"), (16, "RF"), (17, "VM"), (18, "AC"), (21, "ID"),
        ];
        var names = bits.Where(flag => (eflags & (1u << flag.bit)) != 0).Select(flag => flag.name).ToList();
        names.Add($"IOPL={(eflags >> 12) & 3}");
        return string.Join(" ", names);
    }
}
