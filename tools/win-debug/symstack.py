import os,sys,struct
from snapshot import SnapshotReader, snapshot_arguments
if __name__ == '__main__':
    path, arguments = snapshot_arguments(sys.argv[1:], default='sample_vhd.snap')
    with SnapshotReader(path) as snapshot:
        v32, vread = snapshot.v32, snapshot.vread
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
        if arguments:
            for a in arguments: print(f"{a}: {symb(int(a,16))}")
            sys.exit()
        esp=int(os.environ.get('ESP',hex(snapshot.cpu['esp'])),16); TOP=int(os.environ.get('TOP','0x80533a00'),16)
        print(f"exports={len(sym)} esp={esp:08x}")
        for a in range(esp, TOP, 4):
            v=v32(a); s=symb(v)
            if s or (v<0x1000): print(f"  [{a:08x}] {v:08x} {s}")
