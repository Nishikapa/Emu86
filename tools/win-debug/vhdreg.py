import struct, os, sys, pytsk3
class VhdImg(pytsk3.Img_Info):
    def __init__(self, path):
        self.f=open(path,'rb'); self.f.seek(0,2); n=self.f.tell(); self.f.seek(n-512); ft=self.f.read(512)
        assert ft[:8]==b'conectix'
        self.size=struct.unpack('>Q',ft[48:56])[0]; dyn_off=struct.unpack('>Q',ft[16:24])[0]
        self.f.seek(dyn_off); dh=self.f.read(1024); assert dh[:8]==b'cxsparse'
        bat_off=struct.unpack('>Q',dh[16:24])[0]; self.maxbat=struct.unpack('>I',dh[28:32])[0]; self.bs=struct.unpack('>I',dh[32:36])[0]
        self.f.seek(bat_off); self.bat=struct.unpack('>%dI'%self.maxbat,self.f.read(4*self.maxbat))
        self.bitmap=((self.bs//512+7)//8+511)//512*512
        super().__init__(url='')
    def get_size(self): return self.size
    def read(self, off, n):
        out=bytearray()
        while n>0:
            b=off//self.bs; io=off%self.bs; k=min(n,self.bs-io)
            if b<self.maxbat and self.bat[b]!=0xFFFFFFFF:
                self.f.seek(self.bat[b]*512+self.bitmap+io); d=self.f.read(k); out+=d+b'\0'*(k-len(d))
            else: out+=b'\0'*k
            off+=k; n-=k
        return bytes(out)
img=VhdImg('sample.vhd')
fs=pytsk3.FS_Info(img, offset=63*512)
def cat(path):
    f=fs.open(path); return f.read_random(0, f.info.meta.size)
open(os.path.join(os.environ['TEMP'],'SYSTEM'),'wb').write(cat('/WINDOWS/system32/config/system'))
print("boot.ini:"); print(cat('/boot.ini').decode('utf-8','replace'))
print("hive extracted")
