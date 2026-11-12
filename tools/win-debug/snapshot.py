"""Read the CPU/RAM prefix of Emu86 snapshots without restoring guest devices.

CPU.Snapshot.cs fixes this prefix for versions 2 through 14. RAM has no length
field in the format: default CLI snapshots use 256 MiB; set EMU86_RAM_MB for
CLI --ram snapshots, or pass ram_size for injected environments. Physical reads
cover saved RAM, not device MMIO/ROM
aliases. Page walks inspect presence/address bits without modifying access bits
or enforcing guest permissions.
"""

import hashlib
import hmac
import os
from pathlib import Path
import struct


MAGIC = b"EMU86SNP"
HEADER = struct.Struct("<8siq")
CPU_FIELDS = (
    [(name, "H") for name in "cs ds es ss fs gs".split()]
    + [(name + "_base", "I") for name in "cs ds es ss fs gs".split()]
    + [("eip", "I"), ("code32", "?"), ("stack32", "?"),
       ("idt_limit", "H"), ("idt_base", "I"),
       ("gdt_limit", "H"), ("gdt_base", "I")]
    + [(name, "I") for name in "cr0 cr2 cr3 cr4".split()]
    + [("fpu_cw", "H"), ("fpu_sw", "H")]
    + [(name, "I") for name in "ebp esp eflags".split()]
    + [(name + "_prefix", "?") for name in
       "cs es ss ds fs gs operand_size address_size".split()]
    + [(name, "I") for name in "eax ebx ecx edx esi edi".split()]
)
CPU_LAYOUT = struct.Struct("<" + "".join(fmt for _, fmt in CPU_FIELDS))
DEFAULT_RAM_SIZE = 256 * 1024 * 1024
PAGE_SIZE = 0x1000
ADDRESS_SPACE = 1 << 32


class SnapshotError(ValueError):
    """The snapshot or an address cannot be inspected safely."""


class SnapshotReader:
    def __init__(self, path, *, ram_size=None, verify_checksum=False):
        if ram_size is None:
            try:
                ram_size = int(os.environ['EMU86_RAM_MB']) * 1024 * 1024 if 'EMU86_RAM_MB' in os.environ else DEFAULT_RAM_SIZE
            except ValueError as error:
                raise SnapshotError("EMU86_RAM_MB must be a positive integer (MiB)") from error
        if ram_size <= 0:
            raise SnapshotError("RAM size must be positive")
        self.path = Path(path)
        self.ram_size = ram_size
        self.ram_offset = HEADER.size + CPU_LAYOUT.size
        self.file = self.path.open("rb")
        try:
            self.length = self.file.seek(0, os.SEEK_END)
            magic, self.version, self.count = HEADER.unpack(self._read_at(0, HEADER.size))
            if magic != MAGIC:
                raise SnapshotError("Not an Emu86 snapshot")
            if not 2 <= self.version <= 14:
                raise SnapshotError(f"Unsupported snapshot version {self.version}; expected 2..14")
            if self.count < 0:
                raise SnapshotError("Invalid instruction count")
            trailer = 32 if self.version >= 7 else 0
            if self.length < self.ram_offset + ram_size + trailer:
                raise SnapshotError(f"Truncated snapshot or wrong RAM size (expected {ram_size} RAM bytes)")
            values = CPU_LAYOUT.unpack(self._read_at(HEADER.size, CPU_LAYOUT.size))
            self.cpu = dict(zip((name for name, _ in CPU_FIELDS), values))
            self.segments = {name: self.cpu[name] for name in "cs ds es ss fs gs".split()}
            self.segment_bases = {name: self.cpu[name + "_base"] for name in self.segments}
            if verify_checksum:
                self.verify_checksum()
        except BaseException:
            self.close()
            raise

    def close(self):
        self.file.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def _read_at(self, offset, size):
        self.file.seek(offset)
        data = self.file.read(size)
        if len(data) != size:
            raise SnapshotError(f"Truncated snapshot at file offset {offset:#x}")
        return data

    def verify_checksum(self):
        """Validate the full v7+ file, including device state and embedded disk."""
        if self.version < 7:
            raise SnapshotError("Snapshots before v7 do not contain a checksum")
        remaining = self.length - 32
        self.file.seek(0)
        digest = hashlib.sha256()
        while remaining:
            data = self.file.read(min(1024 * 1024, remaining))
            if not data:
                raise SnapshotError("Truncated snapshot while checking SHA-256")
            digest.update(data)
            remaining -= len(data)
        if not hmac.compare_digest(digest.digest(), self.file.read(32)):
            raise SnapshotError("Snapshot checksum mismatch")

    def phys(self, address, size):
        if address < 0 or size < 0 or address > self.ram_size - size:
            raise SnapshotError(f"Physical range {address:#x}+{size:#x} lies outside saved RAM")
        return self._read_at(self.ram_offset + address, size)

    def u32p(self, address):
        return struct.unpack("<I", self.phys(address, 4))[0]

    def _walk(self, linear, cr3=None):
        if not 0 <= linear < ADDRESS_SPACE:
            raise SnapshotError(f"Linear address is outside 32-bit space: {linear:#x}")
        if not self.cpu["cr0"] & 0x80000000:
            return linear, PAGE_SIZE
        root = self.cpu["cr3"] if cr3 is None else cr3
        pae = bool(self.cpu["cr4"] & 0x20)
        table = root & (0xFFFFFFE0 if pae else 0xFFFFF000)
        # shift, index bits, entry size, large-page leaf allowed
        levels = ((30, 2, 8, False), (21, 9, 8, True), (12, 9, 8, False)) if pae else (
            (22, 10, 4, True), (12, 10, 4, False))
        address_mask = 0x000FFFFFFFFFF000 if pae else 0xFFFFF000
        for shift, bits, width, large_allowed in levels:
            offset = table + ((linear >> shift) & ((1 << bits) - 1)) * width
            entry = int.from_bytes(self.phys(offset, width), "little")
            span = 1 << shift
            if not entry & 1:
                return None, span
            if large_allowed and entry & 0x80:
                if not pae and entry & 0x001FE000:
                    raise SnapshotError("PSE-36 physical addresses are not supported")
                return (entry & address_mask & ~(span - 1)) | (linear & (span - 1)), span
            if shift == 12:
                return (entry & address_mask) | (linear & 0xFFF), PAGE_SIZE
            table = entry & address_mask
        raise SnapshotError("Page walk did not reach a leaf")

    def xlat(self, linear, *, cr3=None):
        """Return physical address or None for an absent page (legacy or PAE)."""
        return self._walk(linear, cr3)[0]

    def vread(self, linear, size, *, zero_fill_missing=True):
        if linear < 0 or size < 0 or linear > ADDRESS_SPACE - size:
            raise SnapshotError("Virtual range is outside 32-bit space")
        result = bytearray()
        while size:
            physical = self.xlat(linear)
            count = min(size, PAGE_SIZE - (linear & 0xFFF))
            if physical is None:
                if not zero_fill_missing:
                    raise SnapshotError(f"Virtual page is absent at {linear:#x}")
                result.extend(bytes(count))
            else:
                result.extend(self.phys(physical, count))
            linear += count
            size -= count
        return bytes(result)

    def v32(self, linear):
        return struct.unpack("<I", self.vread(linear, 4))[0]

    def pages(self, *, start=0, stop=0x80000000, cr3=None):
        """Yield (linear, physical) 4 KiB pages, skipping absent table ranges."""
        if start < 0 or start % PAGE_SIZE or stop < start or stop > ADDRESS_SPACE or stop % PAGE_SIZE:
            raise SnapshotError("Page range must be aligned within 32-bit space")
        if not self.cpu["cr0"] & 0x80000000:
            stop = min(stop, self.ram_size & ~(PAGE_SIZE - 1))
        linear = start
        while linear < stop:
            physical, span = self._walk(linear, cr3)
            end = min(stop, (linear & ~(span - 1)) + span)
            if physical is not None:
                if physical < 0 or physical + end - linear > self.ram_size:
                    raise SnapshotError(f"Mapped range at {linear:#x} lies outside saved RAM")
                for page in range(linear, end, PAGE_SIZE):
                    yield page, physical + page - linear
            linear = end


def snapshot_arguments(arguments, *, default=None):
    """Accept SNAP directly, or the old pfdiag.py token plus SNAP environment."""
    arguments = list(arguments)
    if arguments and Path(arguments[0]).name.lower() == "pfdiag.py":
        path = os.environ.get("SNAP", default)
        arguments.pop(0)
    elif arguments:
        path = arguments.pop(0)
    else:
        path = os.environ.get("SNAP", default)
    if not path:
        raise SystemExit("Specify a snapshot path, or set SNAP when using the legacy pfdiag.py argument")
    return path, arguments
