# スナップショット(ユーザーモード停止点)から現在プロセスのユーザー空間を Unicorn にロードし、
# 参照 CPU として 1 命令ずつ実行してレジスタトレースを出す(Emu86 の --trace-all --regtrace と突き合わせる)。
#   SNAP=path python uc_user.py pfdiag.py <stop eip hex> <max instr> > uc_trace.txt
import sys, os, struct
from unicorn import *
from unicorn.x86_const import *
exec(open(sys.argv[1]).read().split("ebp=0x0005ffd0")[0].replace("snap=sys.argv[1]", "snap=os.environ['SNAP']"))
stop_eip = int(sys.argv[2], 16); max_instr = int(sys.argv[3])
# CPU レジスタ
f.seek(20); b = f.read(118); o = 0
def rd(fmt):
    global o; v = struct.unpack_from('<' + fmt, b, o)[0]; o += struct.calcsize('<' + fmt); return v
sr = {n: rd('H') for n in 'cs ds es ss fs gs'.split()}
sb = {n: rd('I') for n in 'cs ds es ss fs gs'.split()}
eip = rd('I'); rd('?'); rd('?'); rd('H'); rd('I'); rd('H'); rd('I'); cr0, cr2, cr3_, cr4 = [rd('I') for _ in range(4)]; rd('H'); rd('H')
ebp, esp, efl = rd('I'), rd('I'), rd('I'); [rd('?') for _ in range(8)]
eax, ebx, ecx, edx, esi, edi = [rd('I') for _ in range(6)]
uc = Uc(UC_ARCH_X86, UC_MODE_32)
# ユーザー空間の存在ページをマップ(PDE 単位で走査)。SNAP2(同じ区間を走った後のスナップショット)が
# あれば、SNAP に無いページ(要求時ページングで後から読まれたもの)をそこから補う。
def snapf_read(snapf, a, n):
    snapf.seek(RAM + a); return snapf.read(n)
def xlat_in(snapf, cr3v, lin):
    pde = struct.unpack('<I', snapf_read(snapf, cr3v + ((lin >> 22) << 2), 4))[0]
    if not pde & 1: return None
    if pde & 0x80: return (pde & 0xffc00000) | (lin & 0x3fffff)
    pte = struct.unpack('<I', snapf_read(snapf, (pde & ~0xfff) + (((lin >> 12) & 0x3ff) << 2), 4))[0]
    if not pte & 1: return None
    return (pte & ~0xfff) | (lin & 0xfff)
def user_pages(snapf, cr3v):
    out = {}
    for pd in range(0, 512):  # 0..0x7fffffff
        pde = struct.unpack('<I', snapf_read(snapf, cr3v + pd * 4, 4))[0]
        if not pde & 1: continue
        for pt in range(1024):
            lin = (pd << 22) | (pt << 12)
            p = xlat_in(snapf, cr3v, lin)
            if p is not None: out[lin] = p
    return out
pages = user_pages(f, cr3)
mapped = 0; supplemented = 0
for lin, p in pages.items():
    uc.mem_map(lin, 0x1000, UC_PROT_ALL); uc.mem_write(lin, phys(p, 0x1000)); mapped += 1
if os.environ.get('SNAP2'):
    f2 = open(os.environ['SNAP2'], 'rb')
    for lin, p in user_pages(f2, cr3).items():
        if lin in pages: continue
        uc.mem_map(lin, 0x1000, UC_PROT_ALL); uc.mem_write(lin, snapf_read(f2, p, 0x1000)); supplemented += 1
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
