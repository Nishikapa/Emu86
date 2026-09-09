import sys,struct
exec(open(sys.argv[1]).read().split("ebp=0x0005ffd0")[0].replace("snap=sys.argv[1]","import os; snap=os.environ.get('SNAP','sample_vhd.snap')"))
base=0x804d7000; pe=base+v32(base+0x3c); expo=v32(pe+0x78)
nf=v32(base+expo+0x14); nn=v32(base+expo+0x18)
funcs=v32(base+expo+0x1c); names=v32(base+expo+0x20); ords=v32(base+expo+0x24)
sym={}
for i in range(nn):
    o=struct.unpack('<H',vread(base+ords+i*2,2))[0]
    rva=v32(base+funcs+o*4); nm=vread(base+v32(base+names+i*4),64).split(b'\0')[0].decode()
    sym[base+rva]=nm
keys=sorted(sym)
import bisect
def symb(a):
    if not (0x804d7000<=a<0x804d7000+0x216700): return ''
    i=bisect.bisect_right(keys,a)-1
    if i<0: return '?'
    return f"{sym[keys[i]]}+{a-keys[i]:x}"
if len(sys.argv)>2:
    for a in sys.argv[2:]: print(f"{a}: {symb(int(a,16))}")
    sys.exit()
f.seek(20+12+24+4+2+12+16+4); ebp,esp=struct.unpack('<II',f.read(8)); esp=int(os.environ.get('ESP',hex(esp)),16); TOP=int(os.environ.get('TOP','0x80533a00'),16)
print(f"exports={len(sym)} esp={esp:08x}")
for a in range(esp, TOP, 4):
    v=v32(a); s=symb(v)
    if s or (v<0x1000): print(f"  [{a:08x}] {v:08x} {s}")
