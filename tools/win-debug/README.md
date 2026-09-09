# Windows XP ブート解析用スクリプト(2026-09-09)

`python`(C:\Python314、capstone / pytsk3 / python-registry 導入済)で動く使い捨てスクリプト。
スナップショット(`*.snap`)の RAM は **ファイル先頭 138 バイト目から**(ヘッダ 20 + CPU 118)。
CR3 はスナップショット先頭から 20+62 バイト目の dword。

| スクリプト | 用途 | 使い方 |
|---|---|---|
| `snapcpu.py` | スナップショットの CPU 状態(セグメント・EIP・EFLAGS・GDT/IDT・CR3・汎用レジスタ)を表示 | `python snapcpu.py sample_vhd.snap` |
| `snapdis.py` | 物理アドレス指定で逆アセンブル(ページング無効時・実モード用) | `python snapdis.py SNAP <phys hex> <len hex> <16|32>` |
| `pfdiag.py` | ページテーブル(非 PAE 2 段)ウォーク付きヘルパ群。他スクリプトが `exec` で取り込む。単体実行はトラップフレーム解析の残骸 | 他から利用 |
| `pfdiag4.py` | 線形アドレス指定で逆アセンブル(ページウォーク経由) | `python pfdiag4.py SNAP pfdiag.py <lin hex> <len hex>` |
| `symstack.py` | ntoskrnl のエクスポートでスタック上の戻り番地をシンボル化 | `SNAP=... ESP=0x... TOP=0x... python symstack.py pfdiag.py`、引数にアドレスを並べると単体シンボル化 |
| `vhdreg.py` | 動的 VHD を pytsk3 でマウントし、NTFS から SYSTEM ハイブと boot.ini を抽出(`%TEMP%\SYSTEM`) | `python vhdreg.py` |
| `regq.py` | 抽出した SYSTEM ハイブから CriticalDeviceDatabase / Enum\PCI / Services を表示 | `python regq.py` |

ntoskrnl のベース(0x804d7000)や KiBugCheckData(0x8055abc0)は sample.vhd の XP に固有の値をハードコードしている。
バグチェック発生時は `KiBugCheckData` の 5 dword(コード + 引数 4 つ)を読むのが最短。
