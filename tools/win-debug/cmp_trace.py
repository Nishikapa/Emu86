# Emu86 の --trace-all --regtrace の trace.log と uc_user.py の出力を突き合わせ、最初に食い違う行を示す。
#   python cmp_trace.py trace.log uc_trace.txt
import sys
a=[l.split() for l in open(sys.argv[1]) if l.startswith('001b:')]  # カーネル側(#PF 処理など)の行は除く
# #PF で命令が再実行されると同じ状態の行が連続して現れるので畳む
a=[x for i,x in enumerate(a) if i==0 or x!=a[i-1]]
# Emu86 は命令実行後の状態(次の EIP)を記録し、Unicorn は実行前の状態を記録するので、Unicorn の先頭行を捨てて揃える。
b=[l.split() for l in open(sys.argv[2]) if l.startswith('001b:')][1:]
names=['eip','eax','ebx','ecx','edx','esi','edi','ebp','esp','efl']
print(f"emu86 {len(a)} lines, unicorn {len(b)} lines")
firstflag=None
for i,(x,y) in enumerate(zip(a,b)):
    if x[0]!=y[0]:
        print(f"CONTROL FLOW diverges at line {i}: emu86 {x[0]} vs unicorn {y[0]}"); print("  prev emu86:", ' '.join(a[i-1])); print("  prev uc   :", ' '.join(b[i-1])); break
    diffs=[(names[k],x[k],y[k]) for k in range(1,10) if x[k]!=y[k]]
    if diffs:
        if all(d[0]=='efl' for d in diffs):
            if firstflag is None: firstflag=(i,x,y)
            continue
        print(f"REGISTER diverges at line {i} ({x[0]}):", diffs); print("  emu86:", ' '.join(x)); print("  uc   :", ' '.join(y)); break
else:
    print("no register divergence in common prefix")
if firstflag: print(f"first EFLAGS-only difference at line {firstflag[0]} ({firstflag[1][0]}): emu86 {firstflag[1][9]} uc {firstflag[2][9]}")
