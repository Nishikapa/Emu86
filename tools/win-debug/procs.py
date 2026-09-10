# スナップショットから XP のプロセス一覧(PsInitialSystemProcess から ActiveProcessLinks を辿る)を表示する。
#   SNAP=path python procs.py pfdiag.py
import sys, os, struct
exec(open(sys.argv[1]).read().split("ebp=0x0005ffd0")[0].replace("snap=sys.argv[1]", "snap=os.environ['SNAP']"))
base = 0x804d7000; pe = base + v32(base + 0x3c); expo = v32(pe + 0x78)
nn = v32(base + expo + 0x18); funcs = v32(base + expo + 0x1c); names = v32(base + expo + 0x20); ords = v32(base + expo + 0x24)
sym = {}
for i in range(nn):
    o = struct.unpack('<H', vread(base + ords + i * 2, 2))[0]
    sym[vread(base + v32(base + names + i * 4), 64).split(b'\0')[0].decode()] = base + v32(base + funcs + o * 4)
sysproc = v32(sym['PsInitialSystemProcess'])
head = v32(sysproc + 0x88 + 4)  # Blink of System = list head(PsActiveProcessHead)のはず
p = sysproc; n = 0
while n < 64:
    pid = v32(p + 0x84); name = vread(p + 0x174, 16).split(b'\0')[0].decode('ascii', 'replace')
    thr = v32(p + 0x1a0)  # ActiveThreads
    print(f"  pid {pid:5d} threads {thr:3d} {name}")
    nxt = v32(p + 0x88) - 0x88; n += 1
    if nxt == sysproc or v32(nxt + 0x84) > 0x10000: break
    p = nxt
