import sys
from pathlib import Path
from snapshot import SnapshotReader


def main():
    import capstone

    path, *arguments = sys.argv[1:]
    if arguments and Path(arguments[0]).name.lower() == "pfdiag.py":
        arguments.pop(0)  # Historical helper filename; no longer executed.
    if len(arguments) != 2:
        raise SystemExit("Usage: python pfdiag4.py SNAP [pfdiag.py] <linear hex> <length hex>")
    address, size = (int(value, 16) for value in arguments)
    with SnapshotReader(path) as snap:
        code = snap.vread(address, size)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    for instruction in md.disasm(code, address):
        print(f"  {instruction.address:08x}: {instruction.bytes.hex():<16} {instruction.mnemonic} {instruction.op_str}")


if __name__ == "__main__":
    main()
