import sys
from snapshot import CPU_LAYOUT, SnapshotReader


def main():
    with SnapshotReader(sys.argv[1]) as snap:
        c = snap.cpu
        print(f"ver={snap.version} count={snap.count} pe={c['cr0'] & 1} pg={c['cr0'] >> 31} code32={c['code32']} stack32={c['stack32']}")
        print("sregs", {k: hex(v) for k, v in snap.segments.items()})
        print("bases", {k: hex(v) for k, v in snap.segment_bases.items()})
        print(f"eip={c['eip']:08x} eflags={c['eflags']:08x} gdt={c['gdt_base']:08x}/{c['gdt_limit']:x} idt={c['idt_base']:08x}/{c['idt_limit']:x} cr3={c['cr3']:08x}")
        print(" ".join(f"{name}={c[name]:08x}" for name in "eax ebx ecx edx esi edi ebp esp".split()))
        print("cpu bytes consumed", CPU_LAYOUT.size, "ram offset", snap.ram_offset)


if __name__ == "__main__":
    main()
