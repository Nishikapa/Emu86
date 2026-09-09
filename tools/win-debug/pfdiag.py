import struct,sys,capstone
snap=sys.argv[1]; RAM=138
f=open(snap,'rb')
def phys(a,n):
    f.seek(RAM+a); return f.read(n)
def u32p(a): return struct.unpack('<I',phys(a,4))[0]
f.seek(20+62); cr3=struct.unpack("<I",f.read(4))[0]
def xlat(lin):
    pde=u32p(cr3+((lin>>22)<<2))
    if not pde&1: return None
    if pde&0x80: return (pde&0xffc00000)|(lin&0x3fffff)
    pte=u32p((pde&~0xfff)+(((lin>>12)&0x3ff)<<2))
    if not pte&1: return None
    return (pte&~0xfff)|(lin&0xfff)
def vread(lin,n):
    out=b''
    while n>0:
        p=xlat(lin)
        k=min(n,0x1000-(lin&0xfff))
        out+= phys(p,k) if p is not None else b'\0'*k
        lin+=k; n-=k
    return out
def v32(lin): return struct.unpack('<I',vread(lin,4))[0]
ebp=0x0005ffd0
names=[(0x30,'gs'),(0x34,'es'),(0x38,'ds'),(0x3c,'edx'),(0x40,'ecx'),(0x44,'eax'),(0x50,'fs'),(0x54,'edi'),(0x58,'esi'),(0x5c,'ebx'),(0x60,'ebp'),(0x64,'err'),(0x68,'eip'),(0x6c,'cs'),(0x70,'eflags')]
print("trap frame:", ' '.join(f"{n}={v32(ebp+o):08x}" for o,n in names))
feip=v32(ebp+0x68)
print("xlat(feip)=",hex(xlat(feip) or 0), " xlat(0x88)=",xlat(0x88), " xlat(0)=",xlat(0))
# module base
b=feip&~0xfff
while b>feip-0x800000:
    if vread(b,2)==b'MZ': break
    b-=0x1000
print("module base", hex(b))
if vread(b,2)==b'MZ':
    pe=b+v32(b+0x3c); print("PE sig",vread(pe,4), "timestamp",hex(v32(pe+8)), "entry",hex(b+v32(pe+0x28)), "sizeofimage",hex(v32(pe+0x50)))
    expo=v32(pe+0x78)
    if expo: print("export name:", vread(b+v32(b+expo+12),32).split(b'\0')[0])
md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_32)
start=feip-0x40; code=vread(start,0x80)
for i in md.disasm(code,start):
    mark='>>' if i.address==feip else '  '
    print(f"{mark}{i.address:08x}: {i.bytes.hex():<16} {i.mnemonic} {i.op_str}")
