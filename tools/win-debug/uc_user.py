# スナップショット(ユーザーモード停止点)から現在プロセスのユーザー空間を Unicorn にロードし、
# 参照 CPU として 1 命令ずつ実行してレジスタトレースを出す(Emu86 の --trace-all --regtrace と突き合わせる)。
#   python uc_user.py SNAP <stop eip hex> <max instr> > uc_trace.txt
#   旧形式 SNAP=path python uc_user.py pfdiag.py ... も受け付ける。
import sys, os, struct
from snapshot import SnapshotReader, snapshot_arguments

if __name__ == '__main__':
    from unicorn import *
    from unicorn.x86_const import *
    path, arguments = snapshot_arguments(sys.argv[1:])
    if len(arguments) != 2:
        raise SystemExit("Usage: python uc_user.py SNAP <stop EIP hex> <max instructions>")
    stop_eip = int(arguments[0], 16); max_instr = int(arguments[1])
    with SnapshotReader(path) as snapshot:
        phys = snapshot.phys
        sb = snapshot.segment_bases
        eip, cr3, efl = (snapshot.cpu[name] for name in ('eip', 'cr3', 'eflags'))
        eax, ebx, ecx, edx, esi, edi, ebp, esp = (snapshot.cpu[name] for name in 'eax ebx ecx edx esi edi ebp esp'.split())
        uc = Uc(UC_ARCH_X86, UC_MODE_32)
        # ユーザー空間の存在ページをマップ(PDE 単位で走査)。SNAP2(同じ区間を走った後のスナップショット)が
        # あれば、SNAP に無いページ(要求時ページングで後から読まれたもの)をそこから補う。
        pages = dict(snapshot.pages())
        mapped = 0; supplemented = 0
        for lin, p in pages.items():
            uc.mem_map(lin, 0x1000, UC_PROT_ALL); uc.mem_write(lin, phys(p, 0x1000)); mapped += 1
        if os.environ.get('SNAP2'):
            with SnapshotReader(os.environ['SNAP2']) as second:
                # Preserve the stopped process's CR3 when looking for pages in SNAP2.
                for lin, p in second.pages(cr3=cr3):
                    if lin in pages: continue
                    uc.mem_map(lin, 0x1000, UC_PROT_ALL); uc.mem_write(lin, second.phys(p, 0x1000)); supplemented += 1
    sys.stderr.write(f"mapped {mapped} pages (+{supplemented} from SNAP2)\n")
    # GDT: フラットな CS/DS/SS と、TEB を指す FS を用意する(32bit では FS_BASE を直接書けない)
    GDT = 0xfffff000
    uc.mem_map(GDT, 0x1000, UC_PROT_ALL)
    def desc(base, limit, flags):
        lo = (limit & 0xffff) | ((base & 0xffff) << 16)
        hi = ((base >> 16) & 0xff) | (flags << 8) | (limit & 0xf0000) | (0xc0 << 16) | (base & 0xff000000)
        return struct.pack('<II', lo, hi)
    # Unicorn の特権チェックを避けるため、記述子は DPL0・セレクタは RPL0 で CPL0 のまま走らせる
    # (ユーザーコードに特権命令は無いので結果は同じ)。
    uc.mem_write(GDT + 0x18, desc(0, 0xfffff, 0x9a))          # 0x18: code
    uc.mem_write(GDT + 0x20, desc(0, 0xfffff, 0x92))          # 0x20: data
    uc.mem_write(GDT + 0x38, desc(sb['fs'], 0xfff, 0x92))     # 0x38: TEB
    uc.reg_write(UC_X86_REG_GDTR, (0, GDT, 0x3f, 0))
    for r, v in [(UC_X86_REG_CS, 0x18), (UC_X86_REG_SS, 0x20), (UC_X86_REG_DS, 0x20), (UC_X86_REG_ES, 0x20), (UC_X86_REG_FS, 0x38)]:
        try:
            uc.reg_write(r, v)
        except UcError as e:
            sys.stderr.write(f"segment write {r} failed: {e}\n")
    for r, v in [(UC_X86_REG_EAX, eax), (UC_X86_REG_EBX, ebx), (UC_X86_REG_ECX, ecx), (UC_X86_REG_EDX, edx), (UC_X86_REG_ESI, esi), (UC_X86_REG_EDI, edi), (UC_X86_REG_EBP, ebp), (UC_X86_REG_ESP, esp), (UC_X86_REG_EFLAGS, efl | 2)]:
        uc.reg_write(r, v)
    n = 0
    def hook_code(uc, address, size, user_data):
        global n
        if n >= max_instr or address == stop_eip:
            uc.emu_stop(); return
        n += 1
        regs = [uc.reg_read(r) for r in (UC_X86_REG_EAX, UC_X86_REG_EBX, UC_X86_REG_ECX, UC_X86_REG_EDX, UC_X86_REG_ESI, UC_X86_REG_EDI, UC_X86_REG_EBP, UC_X86_REG_ESP, UC_X86_REG_EFLAGS)]
        print(f"001b:{address:08x} " + ' '.join(f"{v:08x}" for v in regs))
    def hook_mem_invalid(uc, access, address, size, value, user_data):
        sys.stderr.write(f"unmapped access type {access} at {address:08x} (eip {uc.reg_read(UC_X86_REG_EIP):08x}, instr {n})\n"); return False
    def hook_intr(uc, intno, user_data):
        sys.stderr.write(f"interrupt {intno} at {uc.reg_read(UC_X86_REG_EIP):08x} after {n} instr\n"); uc.emu_stop()
    uc.hook_add(UC_HOOK_CODE, hook_code)
    uc.hook_add(UC_HOOK_INTR, hook_intr)
    uc.hook_add(UC_HOOK_MEM_READ_UNMAPPED | UC_HOOK_MEM_WRITE_UNMAPPED, hook_mem_invalid)
    try:
        uc.emu_start(eip, 0xffffffff)
    except UcError as e:
        sys.stderr.write(f"UcError {e} at {uc.reg_read(UC_X86_REG_EIP):08x} after {n} instr\n")
    sys.stderr.write(f"done {n} instr, eip={uc.reg_read(UC_X86_REG_EIP):08x}\n")
