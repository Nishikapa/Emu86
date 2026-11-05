<#
.SYNOPSIS
  作業ツリーの通常コアを、基準コミット(既定 HEAD)と差分実行して比べる。
.DESCRIPTION
  基準コミットを git worktree として一時フォルダに取り出し、同じハーネス(Diff.cs)を基準側と作業ツリー側の
  Emu86.csproj に対してそれぞれビルド・実行し、出力(1 ケース 1 行)を Compare-Object で比べる。
  差分が無ければ終了コード 0。有れば差分行(先頭 20 行)を表示し、出力ファイルを残して 1 を返す。
.PARAMETER Base
  基準の git リビジョン(既定 HEAD)。
.PARAMETER Keep
  差分が無くても一時フォルダ(両方の出力と成果物)を残す。
.EXAMPLE
  pwsh tools/diff-harness/Compare.ps1
  pwsh tools/diff-harness/Compare.ps1 -Base HEAD~1
#>
param(
    [string]$Base = "HEAD",
    [switch]$Keep
)

$harness = $PSScriptRoot
$root = (Resolve-Path (Join-Path $harness "..\..")).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("emu86-diff-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$baseTree = Join-Path $work "base"
New-Item -ItemType Directory -Path $work | Out-Null

# ハーネスのプロジェクトを一時フォルダへコピーし(bin/obj をリポジトリの外に置く)、project の本体に対してビルドして exe のパスを返す。
# 本体の bin/obj はいつもの場所(作業ツリー側はリポジトリ、基準側は worktree)。
function Build-Harness([string]$label, [string]$project) {
    $dir = Join-Path $work "$label-harness"
    New-Item -ItemType Directory -Path $dir | Out-Null
    Copy-Item (Join-Path $harness "Emu86.Diff.csproj") $dir
    $log = & dotnet build (Join-Path $dir "Emu86.Diff.csproj") -c Release --nologo "-p:HarnessDir=$harness" "-p:Emu86Project=$project" 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Out-Host; throw "build failed: $label" }
    (Get-ChildItem -Path (Join-Path $dir "bin") -Recurse -Filter Emu86.Tests.exe | Select-Object -First 1).FullName
}

# ハーネスを実行し、出力ファイルのパスを返す。
function Invoke-Harness([string]$label, [string]$exe) {
    $out = Join-Path $work "$label.txt"
    & $exe $out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "harness failed: $label" }
    $out
}

& git -C $root worktree add --detach $baseTree $Base | Out-Host
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed: $Base" }
try {
    $baseOut = Invoke-Harness "base" (Build-Harness "base" (Join-Path $baseTree "Emu86.csproj"))
    $newOut = Invoke-Harness "new" (Build-Harness "new" (Join-Path $root "Emu86.csproj"))
}
catch {
    Write-Host "kept: $work"
    throw
}
finally {
    & git -C $root worktree remove --force $baseTree | Out-Null
}

$baseLines = @(Get-Content $baseOut)
$newLines = @(Get-Content $newOut)
$differences = @(Compare-Object -ReferenceObject $baseLines -DifferenceObject $newLines)
Write-Host ("{0} cases ({1}: {2}, working tree: {3}), {4} differences" -f $newLines.Count, $Base, $baseLines.Count, $newLines.Count, $differences.Count)
foreach ($difference in $differences | Select-Object -First 20) {
    Write-Host ("  {0} {1}" -f $difference.SideIndicator, $difference.InputObject)
}
if ($Keep -or $differences.Count -gt 0) { Write-Host "outputs: $work" } else { Remove-Item -Recurse -Force $work }
if ($differences.Count -gt 0) { exit 1 }
exit 0
