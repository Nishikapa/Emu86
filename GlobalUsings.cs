// プロジェクト全体で使う型エイリアス。
// Data    : 幅付きオペランド値(type: 0=byte, 1=word, 2=dword)
// MemAddr : メモリ(isMem=true, 物理アドレス) または レジスタ(isMem=false, レジスタ番号)
global using Data = (int type, byte db, ushort dw, uint dd);
global using MemAddr = (bool isMem, uint addr);
