import struct,sys
f=open(sys.argv[1],'rb'); f.seek(8); ver,count=struct.unpack('<iq',f.read(12))
b=f.read(200); o=0
def rd(fmt):
    global o; v=struct.unpack_from('<'+fmt,b,o)[0]; o+=struct.calcsize('<'+fmt); return v
sr={n:rd('H') for n in 'cs ds es ss fs gs'.split()}
sb={n:rd('I') for n in 'cs ds es ss fs gs'.split()}
eip=rd('I'); code32=rd('?'); stack32=rd('?')
idtl=rd('H'); idtb=rd('I'); gdtl=rd('H'); gdtb=rd('I')
cr0,cr2,cr3,cr4=[rd('I') for _ in range(4)]; fcw,fsw=rd('H'),rd('H')
ebp,esp,efl=rd('I'),rd('I'),rd('I'); pref=[rd('?') for _ in range(8)]
eax,ebx,ecx,edx,esi,edi=[rd('I') for _ in range(6)]
print(f"ver={ver} count={count} pe={cr0&1} pg={cr0>>31} code32={code32} stack32={stack32}")
print("sregs",{k:hex(v) for k,v in sr.items()}); print("bases",{k:hex(v) for k,v in sb.items()})
print(f"eip={eip:08x} eflags={efl:08x} gdt={gdtb:08x}/{gdtl:x} idt={idtb:08x}/{idtl:x} cr3={cr3:08x}")
print(f"eax={eax:08x} ebx={ebx:08x} ecx={ecx:08x} edx={edx:08x} esi={esi:08x} edi={edi:08x} ebp={ebp:08x} esp={esp:08x}")
print("cpu bytes consumed", o, "ram offset", 20+o)
