"""Synthetic snapshot/page-table regressions; Python standard library only."""

import hashlib
import importlib
import io
import os
from pathlib import Path
import runpy
import struct
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from types import SimpleNamespace
from unittest.mock import Mock, patch

from snapshot import SnapshotError, SnapshotReader, snapshot_arguments


def cpu_bytes(*, cr0=0x80000001, cr3=0x1000, cr4=0, esp=0x87654321):
    # Independent encoding of CPU.Snapshot.cs's fixed v2+ register prefix.
    return (
        struct.pack('<6H', 8, 16, 24, 32, 40, 48)
        + struct.pack('<6I', 0, 0x100, 0x200, 0x300, 0x400, 0x500)
        + struct.pack('<I??HIHI', 0x80401000, True, False, 0x7ff, 0x9000, 0x3ff, 0xa000)
        + struct.pack('<4IHH3I', cr0, 0x11223344, cr3, cr4, 0x37f, 0x1234, 0x12345678, esp, 0x202)
        + struct.pack('<8?', True, False, True, False, True, False, True, False)
        + struct.pack('<6I', 1, 2, 3, 4, 5, 6)
    )


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / 'synthetic.snap'

    def write(self, ram=None, *, version=14, count=123, **cpu):
        ram = bytearray(0x10000) if ram is None else ram
        data = b'EMU86SNP' + struct.pack('<iq', version, count) + cpu_bytes(**cpu) + ram
        if version >= 7:
            data += hashlib.sha256(data).digest()
        self.path.write_bytes(data)
        return self.path

    def reader(self, ram_size=0x10000, **options):
        reader = SnapshotReader(self.path, ram_size=ram_size, **options)
        self.addCleanup(reader.close)
        return reader

    def test_register_layout_matches_saved_cpu(self):
        self.write()
        snap = self.reader()
        self.assertEqual((14, 123, 138), (snap.version, snap.count, snap.ram_offset))
        self.assertEqual({'cs': 8, 'ds': 16, 'es': 24, 'ss': 32, 'fs': 40, 'gs': 48}, snap.segments)
        self.assertEqual(0x400, snap.segment_bases['fs'])
        self.assertEqual((0x80401000, 0x87654321, 0x12345678, 0x202, 0x11223344),
                         tuple(snap.cpu[name] for name in ('eip', 'esp', 'ebp', 'eflags', 'cr2')))
        self.assertEqual((True, False, 0x9000, 0xa000, 1, 6),
                         tuple(snap.cpu[name] for name in ('code32', 'stack32', 'idt_base', 'gdt_base', 'eax', 'edi')))

    def test_supported_versions(self):
        for version in range(2, 15):
            with self.subTest(version=version):
                self.write(version=version)
                with SnapshotReader(self.path, ram_size=0x10000) as snap:
                    self.assertEqual(version, snap.version)

    def test_unknown_versions_and_negative_count(self):
        for version in (1, 15):
            self.write(version=version)
            with self.assertRaisesRegex(SnapshotError, 'version'):
                self.reader()
        self.write(count=-1)
        with self.assertRaisesRegex(SnapshotError, 'instruction count'):
            self.reader()

    def test_bad_magic_and_truncated_regions(self):
        self.write()
        complete = self.path.read_bytes()
        self.path.write_bytes(b'WRONGSNP' + complete[8:])
        with self.assertRaisesRegex(SnapshotError, 'Not an Emu86'):
            self.reader()
        for length in (0, 19, 20, 137, 138 + 0x10000 - 1, len(complete) - 1):
            with self.subTest(length=length):
                self.path.write_bytes(complete[:length])
                with self.assertRaisesRegex(SnapshotError, 'Truncated'):
                    self.reader()

    def test_checksum_and_legacy_without_digest(self):
        self.write()
        self.reader(verify_checksum=True)
        corrupt = bytearray(self.path.read_bytes())
        corrupt[138 + 10] ^= 1
        self.path.write_bytes(corrupt)
        with self.assertRaisesRegex(SnapshotError, 'checksum mismatch'):
            self.reader(verify_checksum=True)
        self.write(version=6)
        with self.assertRaisesRegex(SnapshotError, 'do not contain a checksum'):
            self.reader(verify_checksum=True)

    def test_physical_bounds(self):
        self.write()
        snap = self.reader()
        for address, size in ((-1, 1), (0, -1), (0xffff, 2), (0x10001, 0)):
            with self.assertRaises(SnapshotError):
                snap.phys(address, size)
        self.assertEqual(b'', snap.phys(0x10000, 0))

    def test_ram_size_environment_for_cli_snapshots(self):
        self.write(bytearray(1024 * 1024))
        with patch.dict(os.environ, {'EMU86_RAM_MB': '1'}):
            with SnapshotReader(self.path) as snap:
                self.assertEqual(1024 * 1024, snap.ram_size)
        self.write()
        with patch.dict(os.environ, {'EMU86_RAM_MB': '1024'}):
            self.assertEqual(0x10000, self.reader().ram_size)
        for value in ('invalid', '0', '-1'):
            with patch.dict(os.environ, {'EMU86_RAM_MB': value}):
                with self.assertRaises(SnapshotError):
                    SnapshotReader(self.path)

    def test_snapcpu_cli_on_synthetic_small_ram(self):
        self.write(bytearray(1024 * 1024))
        environment = dict(os.environ, EMU86_RAM_MB='1', PYTHONIOENCODING='utf-8')
        result = subprocess.run([sys.executable, str(Path(__file__).with_name('snapcpu.py')), str(self.path)],
                                env=environment, capture_output=True, text=True, check=True)
        self.assertIn('ver=14 count=123 pe=1 pg=1 code32=True stack32=False', result.stdout)
        self.assertIn('ebp=12345678 esp=87654321', result.stdout)
        self.assertIn('cpu bytes consumed 118 ram offset 138', result.stdout)

    def test_exception_and_trap_disassembly_at_address_space_edges(self):
        # Run the actual CLIs with synthetic exception/trap frames. Capstone is
        # replaced only at its disassembly boundary, leaving snapshot reads real.
        for script, lookbehind, window in (('uexc.py', 0x20, 0x40), ('pfdiag.py', 0x40, 0x80)):
            for address in (0, 0x10, 0xffffffff):
                with self.subTest(script=script, address=address):
                    ram = bytearray(1024 * 1024)
                    struct.pack_into('<I', ram, 0x1000, 0x2001)
                    for page in range(256):
                        struct.pack_into('<I', ram, 0x2000 + page * 4, page * 0x1000 | 1)
                    struct.pack_into('<I', ram, 0x1ffc, 0x3001)
                    struct.pack_into('<I', ram, 0x3ffc, 0x5001)
                    struct.pack_into('<II', ram, 0x10000, 0x20000, 0x30000)
                    struct.pack_into('<7I', ram, 0x20000, 0xc0000005, 0, 0, address, 2, 8, address)
                    context = [0] * 12
                    context[7], context[10] = address, 0x40000
                    struct.pack_into('<12I', ram, 0x3009c, *context)
                    struct.pack_into('<I', ram, 0x5ffd0 + 0x68, address)
                    self.write(ram, esp=0x10000)
                    decoder = Mock()
                    decoder.disasm.return_value = []
                    capstone = SimpleNamespace(Cs=lambda *_: decoder, CS_ARCH_X86=0, CS_MODE_32=32)
                    output = io.StringIO()
                    path = str(Path(__file__).with_name(script))
                    with patch.dict(os.environ, {'EMU86_RAM_MB': '1'}), \
                         patch.dict(sys.modules, {'capstone': capstone}), \
                         patch.object(sys, 'argv', [path, str(self.path)]), redirect_stdout(output):
                        runpy.run_path(path, run_name='__main__')
                    code, start = decoder.disasm.call_args.args
                    self.assertEqual(max(0, address - lookbehind), start)
                    self.assertEqual(min(window, (1 << 32) - start), len(code))
                    self.assertIn('stack at Esp:' if script == 'uexc.py' else 'trap frame:', output.getvalue())

    def test_paging_disabled(self):
        ram = bytearray(0x10000)
        ram[0x1234:0x1238] = b'flat'
        self.write(ram, cr0=1)
        snap = self.reader()
        self.assertEqual(0x1234, snap.xlat(0x1234))
        self.assertEqual(b'flat', snap.vread(0x1234, 4))
        self.assertEqual(16, len(list(snap.pages())))

    def legacy(self):
        ram = bytearray(0x10000)
        struct.pack_into('<I', ram, 0x1000, 0x2001)
        struct.pack_into('<II', ram, 0x2000, 0x3001, 0x6001)
        ram[0x3ff0:0x4000] = b'A' * 16
        ram[0x6000:0x6010] = b'B' * 16
        self.write(ram, cr3=0x1018)  # CR3 PWT/PCD flags are not address bits.
        return self.reader()

    def test_legacy_translation_and_noncontiguous_page_boundary(self):
        snap = self.legacy()
        self.assertEqual(0x3123, snap.xlat(0x123))
        self.assertEqual(b'A' * 16 + b'B' * 16, snap.vread(0xff0, 32))
        self.assertEqual([(0, 0x3000), (0x1000, 0x6000)], list(snap.pages(stop=0x800000)))

    def test_absent_pages_zero_fill_or_reject(self):
        snap = self.legacy()
        self.assertIsNone(snap.xlat(0x2000))
        self.assertIsNone(snap.xlat(0x400000))
        self.assertEqual(bytes(32), snap.vread(0x1ff0, 32))
        with self.assertRaisesRegex(SnapshotError, 'absent'):
            snap.vread(0x2000, 1, zero_fill_missing=False)

    def test_legacy_large_page(self):
        ram = bytearray(0x10000)
        struct.pack_into('<I', ram, 0x1004, 0x81)
        self.write(ram)
        snap = self.reader()
        self.assertEqual(0x1234, snap.xlat(0x401234))
        self.assertEqual([(0x400000, 0), (0x401000, 0x1000)],
                         list(snap.pages(start=0x400000, stop=0x402000)))

    def test_pae_translation_large_page_and_nx(self):
        ram = bytearray(3 * 1024 * 1024)
        struct.pack_into('<Q', ram, 0x1030, 0x2001)  # PDPT index 2, CR3 is 32-byte aligned.
        struct.pack_into('<QQ', ram, 0x2000, 0x3001, 0x200081)
        struct.pack_into('<QQ', ram, 0x3000, (1 << 63) | 0x6001, 0x9001)
        ram[0x6ff0:0x7000] = b'C' * 16
        ram[0x9000:0x9010] = b'D' * 16
        self.write(ram, cr3=0x1020, cr4=0x20)
        snap = self.reader(ram_size=len(ram))
        self.assertEqual(0x6123, snap.xlat(0x80000123))
        self.assertEqual(0x200123, snap.xlat(0x80200123))
        self.assertEqual(b'C' * 16 + b'D' * 16, snap.vread(0x80000ff0, 32))
        self.assertIsNone(snap.xlat(0x40000000))
        self.assertEqual([(0x80000000, 0x6000), (0x80001000, 0x9000)],
                         list(snap.pages(start=0x80000000, stop=0x80200000)))

    def test_other_cr3_for_supplementary_snapshot(self):
        ram = bytearray(0x10000)
        struct.pack_into('<I', ram, 0x4000, 0x5001)
        struct.pack_into('<I', ram, 0x5000, 0x7001)
        self.write(ram)
        snap = self.reader()
        self.assertIsNone(snap.xlat(0x123))
        self.assertEqual(0x7123, snap.xlat(0x123, cr3=0x4018))
        self.assertEqual([(0, 0x7000)], list(snap.pages(stop=0x400000, cr3=0x4000)))

    def test_invalid_linear_ranges_and_unstored_physical_pages(self):
        snap = self.legacy()
        for linear, size in ((-1, 1), (0, -1), (0xffffffff, 2)):
            with self.assertRaises(SnapshotError):
                snap.vread(linear, size)
        with self.assertRaises(SnapshotError):
            snap.xlat(1 << 32)
        with self.assertRaises(SnapshotError):
            list(snap.pages(start=1))
        snap.close()
        ram = bytearray(0x10000)
        struct.pack_into('<I', ram, 0x1000, 0x2001)
        struct.pack_into('<I', ram, 0x2000, 0x100001)
        self.write(ram)
        with self.assertRaisesRegex(SnapshotError, 'outside saved RAM'):
            self.reader().vread(0, 1)
        with self.assertRaisesRegex(SnapshotError, 'outside saved RAM'):
            list(self.reader().pages())

    def test_cli_direct_and_legacy_paths(self):
        self.assertEqual(('sample.snap', ['123']), snapshot_arguments(['sample.snap', '123']))
        with patch.dict(os.environ, {'SNAP': 'legacy.snap'}):
            self.assertEqual(('legacy.snap', ['123']), snapshot_arguments(['some/path/pfdiag.py', '123']))
        with patch.dict(os.environ, {}, clear=True):
            with self.assertRaises(SystemExit):
                snapshot_arguments(['pfdiag.py'])

    def test_scripts_import_without_cli_or_optional_packages(self):
        for name in ('snapcpu', 'snapdis', 'pfdiag', 'pfdiag4', 'symstack', 'procs', 'uexc', 'uc_user'):
            with self.subTest(module=name):
                importlib.import_module(name)


if __name__ == '__main__':
    unittest.main()
