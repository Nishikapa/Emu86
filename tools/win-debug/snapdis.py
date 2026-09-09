import sys,capstone
snap,phys,n,mode=sys.argv[1],int(sys.argv[2],16),int(sys.argv[3],16),sys.argv[4]
f=open(snap,'rb'); f.seek(138+phys); code=f.read(n)
md=capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16 if mode=='16' else capstone.CS_MODE_32)
for i in md.disasm(code,phys): print(f"{i.address:06x}: {i.bytes.hex():<14} {i.mnemonic} {i.op_str}")
