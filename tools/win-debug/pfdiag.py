"""Inspect the XP trap frame; reusable snapshot operations live in snapshot.py."""
import sys
from snapshot import SnapshotReader


def main():
    import capstone

    if len(sys.argv) not in (2, 3):
        raise SystemExit("Usage: python pfdiag.py SNAP [trap-frame EBP hex]")
    ebp = int(sys.argv[2], 16) if len(sys.argv) == 3 else 0x0005FFD0
    with SnapshotReader(sys.argv[1]) as snap:
        v32, vread, xlat = snap.v32, snap.vread, snap.xlat
        names = [(0x30, 'gs'), (0x34, 'es'), (0x38, 'ds'), (0x3c, 'edx'),
                 (0x40, 'ecx'), (0x44, 'eax'), (0x50, 'fs'), (0x54, 'edi'),
                 (0x58, 'esi'), (0x5c, 'ebx'), (0x60, 'ebp'), (0x64, 'err'),
                 (0x68, 'eip'), (0x6c, 'cs'), (0x70, 'eflags')]
        print("trap frame:", ' '.join(f"{name}={v32(ebp + offset):08x}" for offset, name in names))
        feip = v32(ebp + 0x68)
        print("xlat(feip)=", hex(xlat(feip) or 0), " xlat(0x88)=", xlat(0x88), " xlat(0)=", xlat(0))
        base = feip & ~0xfff
        while base >= 0 and base > feip - 0x800000:
            if vread(base, 2) == b'MZ':
                break
            base -= 0x1000
        print("module base", hex(base))
        if base >= 0 and vread(base, 2) == b'MZ':
            pe = base + v32(base + 0x3c)
            print("PE sig", vread(pe, 4), "timestamp", hex(v32(pe + 8)), "entry", hex(base + v32(pe + 0x28)), "sizeofimage", hex(v32(pe + 0x50)))
            export = v32(pe + 0x78)
            if export:
                print("export name:", vread(base + v32(base + export + 12), 32).split(b'\0')[0])
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        start = max(0, feip - 0x40)
        size = min(0x80, (1 << 32) - start)
        for instruction in md.disasm(vread(start, size), start):
            mark = '>>' if instruction.address == feip else '  '
            print(f"{mark}{instruction.address:08x}: {instruction.bytes.hex():<16} {instruction.mnemonic} {instruction.op_str}")


if __name__ == "__main__":
    main()
