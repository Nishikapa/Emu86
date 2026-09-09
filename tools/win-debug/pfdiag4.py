import struct,sys,capstone
exec(open(sys.argv[2]).read().split("ebp=0x0005ffd0")[0])
md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_32)
a=int(sys.argv[3],16); n=int(sys.argv[4],16); code=vread(a,n)
for i in md.disasm(code,a): print(f"  {i.address:08x}: {i.bytes.hex():<16} {i.mnemonic} {i.op_str}")
