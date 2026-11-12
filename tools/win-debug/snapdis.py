import sys
from snapshot import SnapshotReader


def main():
    import capstone

    path, physical, size, mode = sys.argv[1], int(sys.argv[2], 16), int(sys.argv[3], 16), sys.argv[4]
    with SnapshotReader(path) as snap:
        code = snap.phys(physical, size)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16 if mode == '16' else capstone.CS_MODE_32)
    for instruction in md.disasm(code, physical):
        print(f"{instruction.address:06x}: {instruction.bytes.hex():<14} {instruction.mnemonic} {instruction.op_str}")


if __name__ == "__main__":
    main()
