# KiUserExceptionDispatcher 突入時点のスナップショットから EXCEPTION_RECORD / CONTEXT を読み、例外発生箇所を逆アセンブルする。
#   python uexc.py SNAP (旧: SNAP=path python uexc.py pfdiag.py)
import sys, struct
from snapshot import SnapshotReader, snapshot_arguments
if __name__ == '__main__':
    import capstone
    path, arguments = snapshot_arguments(sys.argv[1:])
    if arguments: raise SystemExit("Usage: python uexc.py SNAP")
    with SnapshotReader(path) as snapshot:
        v32, vread = snapshot.v32, snapshot.vread
        cr3, esp = snapshot.cpu['cr3'], snapshot.cpu['esp']
        print(f"cr3={cr3:08x} esp={esp:08x}")
        rec = v32(esp); ctx = v32(esp + 4)
        code, flags, nested, addr, nparams = struct.unpack('<IIIII', vread(rec, 20))
        info = [v32(rec + 20 + i * 4) for i in range(min(nparams, 4))]
        print(f"EXCEPTION_RECORD @{rec:08x}: code={code:08x} flags={flags:x} addr={addr:08x} nparams={nparams} info={[hex(x) for x in info]}")
        names = ['Edi', 'Esi', 'Ebx', 'Edx', 'Ecx', 'Eax', 'Ebp', 'Eip', 'SegCs', 'EFlags', 'Esp', 'SegSs']
        vals = struct.unpack('<12I', vread(ctx + 0x9c, 48))
        print("CONTEXT:", ' '.join(f"{n}={v:08x}" for n, v in zip(names, vals)))
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        start = max(0, addr - 0x20)
        size = min(0x40, (1 << 32) - start)
        for i in md.disasm(vread(start, size), start):
            print(f"{'>>' if i.address == addr else '  '}{i.address:08x}: {i.bytes.hex():<14} {i.mnemonic} {i.op_str}")
        print("stack at Esp:", ' '.join(f"{v32(vals[10] + k * 4):08x}" for k in range(12)))
