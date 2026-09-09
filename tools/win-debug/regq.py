import os
from Registry import Registry
BS=chr(92)
def P(*parts): return BS.join(parts)
r=Registry.Registry(os.path.join(os.environ['TEMP'],'SYSTEM'))
cur=r.open('Select').value('Current').value(); cs='ControlSet%03d'%cur; print("current",cs)
cddb=r.open(P(cs,'Control','CriticalDeviceDatabase'))
print("CDDB entries with ven_8086 / cc_01:")
for k in cddb.subkeys():
    n=k.name().lower()
    if 'ven_8086' in n or 'cc_01' in n:
        try: svc=k.value('Service').value()
        except: svc='?'
        print("  ",n,"->",svc)
print("Enum PCI:")
for k in r.open(P(cs,'Enum','PCI')).subkeys():
    for inst in k.subkeys():
        vals={v.name():v.value() for v in inst.values()}
        print("  ",k.name(),inst.name(),"Service=",vals.get('Service'),"ConfigFlags=",vals.get('ConfigFlags'))
for s in ['intelide','pciide','atapi','disk','pciidex']:
    try: k=r.open(P(cs,'Services',s)); print(s,"Start=",k.value('Start').value(),"Group=",k.value('Group').value())
    except Exception as e: print(s,"missing",e)
