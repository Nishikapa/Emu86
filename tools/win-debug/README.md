# Windows XP ブート解析用スクリプト

Python 用のスナップショット解析ツール。逆アセンブルには `capstone`、参照実行には `unicorn`、
VHD/レジストリ解析には `pytsk3` / `python-registry` が必要です。
CPU・RAM・ページテーブルの読み取りは標準ライブラリだけの `snapshot.py` に集約しています。
他のスクリプトのソースを加工して `exec` する必要はありません。

読み取り対象は保存形式 v2〜v14 の固定 CPU/RAM 部分です。マジック・版・命令数・RAM 部分の長さを検証し、
範囲外の読み取りはエラーにします。保存形式に RAM 長が無いため、既定値は CLI と同じ **256 MiB**。
`--ram 16` で作ったスナップショットには、PowerShell で `$env:EMU86_RAM_MB='16'` を設定してから実行してください
(解析後は `Remove-Item Env:EMU86_RAM_MB` で解除)。API では `SnapshotReader(path, ram_size=...)` でバイト数を指定できます。
256 MiB より大きい場合も同様に保存時の値を指定してください。

非 PAE の 4 KiB/4 MiB ページと PAE の 4 KiB/2 MiB ページを扱い、ページング無効時は恒等変換します。
非 PAE の PSE-36 は明示的に拒否します。物理読み取りは保存された RAM に限り、MMIO・ROM エイリアスや
ゲストのアクセス権限検査は再現しません。未割り当ての仮想ページは従来どおりゼロで埋めます
(`vread(..., zero_fill_missing=False)` ならエラー)。

デバイス状態・埋め込みディスクの解釈は行いません。全ファイルの整合性も検証する場合は
`SnapshotReader(path, verify_checksum=True)` を使用します(v7 以降の末尾 SHA-256 を確認)。
既定では巨大な埋め込みディスク全体のハッシュ計算を省き、CPU/RAM 部分だけを検証します。

| スクリプト | 用途 | 使い方 |
|---|---|---|
| `snapcpu.py` | スナップショットの CPU 状態(セグメント・EIP・EFLAGS・GDT/IDT・CR3・汎用レジスタ)を表示 | `python snapcpu.py sample_vhd.snap` |
| `snapdis.py` | 物理アドレス指定で逆アセンブル(ページング無効時・実モード用) | `python snapdis.py SNAP <phys hex> <len hex> <16|32>` |
| `pfdiag.py` | XP のトラップフレームと障害箇所を表示 | `python pfdiag.py SNAP [trap-frame EBP hex]` (既定 EBP は以前と同じ `0x0005ffd0`) |
| `pfdiag4.py` | 線形アドレス指定で逆アセンブル(ページウォーク経由) | `python pfdiag4.py SNAP <lin hex> <len hex>` |
| `symstack.py` | ntoskrnl のエクスポートでスタック上の戻り番地をシンボル化 | `python symstack.py SNAP [address hex ...]`。環境変数 `ESP` / `TOP` で走査範囲を指定 |
| `procs.py` | XP の ActiveProcessLinks からプロセス一覧を表示 | `python procs.py SNAP` |
| `uexc.py` | KiUserExceptionDispatcher の EXCEPTION_RECORD / CONTEXT と障害箇所を表示 | `python uexc.py SNAP` |
| `uc_user.py` | ユーザー空間を Unicorn に読み込んで比較用トレースを出力 | `python uc_user.py SNAP <stop EIP hex> <max instructions>`。`SNAP2` から不足ページを補完可能 |
| `vhdreg.py` | 動的 VHD を pytsk3 でマウントし、NTFS から SYSTEM ハイブと boot.ini を抽出(`%TEMP%\SYSTEM`) | `python vhdreg.py` |
| `regq.py` | 抽出した SYSTEM ハイブから CriticalDeviceDatabase / Enum\PCI / Services を表示 | `python regq.py` |

ntoskrnl のベース(0x804d7000)や KiBugCheckData(0x8055abc0)は sample.vhd の XP に固有の値をハードコードしている。
バグチェック発生時は `KiBugCheckData` の 5 dword(コード + 引数 4 つ)を読むのが最短。

旧形式の `python pfdiag4.py SNAP pfdiag.py ...`、および `SNAP` 環境変数と組み合わせる
`python procs.py pfdiag.py` / `symstack.py pfdiag.py ...` / `uexc.py pfdiag.py` / `uc_user.py pfdiag.py ...`
も受け付けます。`pfdiag.py` 引数は互換用の目印として無視され、読み込み・実行されません。

共通 reader の合成データによるテスト(実際のディスク・スナップショットや追加パッケージは不要):

```powershell
python -m unittest discover -s tools/win-debug -p test_snapshot.py -v
```
