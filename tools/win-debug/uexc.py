# KiUserExceptionDispatcher 突入時点のスナップショットから EXCEPTION_RECORD / CONTEXT を読み、例外発生箇所を逆アセンブルする。
#   SNAP=path python uexc.py pfdiag.py
import sys, os, struct, capstone
exec(open(sys.argv[1]).read().split("ebp=0x0005ffd0")[0].replace("snap=sys.argv[1]", "snap=os.environ['SNAP']"))
f.seek(20 + 12 + 24 + 4 + 2 + 12 + 16 + 4); ebp, esp = struct.unpack('<II', f.read(8))
print(f"cr3={cr3:08x} esp={esp:08x}")
rec = v32(esp); ctx = v32(esp + 4)
code, flags, nested, addr, nparams = struct.unpack('<IIIII', vread(rec, 20))
info = [v32(rec + 20 + i * 4) for i in range(min(nparams, 4))]
print(f"EXCEPTION_RECORD @{rec:08x}: code={code:08x} flags={flags:x} addr={addr:08x} nparams={nparams} info={[hex(x) for x in info]}")
names = ['Edi', 'Esi', 'Ebx', 'Edx', 'Ecx', 'Eax', 'Ebp', 'Eip', 'SegCs', 'EFlags', 'Esp', 'SegSs']
vals = struct.unpack('<12I', vread(ctx + 0x9c, 48))
print("CONTEXT:", ' '.join(f"{n}={v:08x}" for n, v in zip(names, vals)))
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
start = addr - 0x20
for i in md.disasm(vread(start, 0x40), start):
    print(f"{'>>' if i.address == addr else '  '}{i.address:08x}: {i.bytes.hex():<14} {i.mnemonic} {i.op_str}")
print("stack at Esp:", ' '.join(f"{v32(vals[10] + k * 4):08x}" for k in range(12)))
