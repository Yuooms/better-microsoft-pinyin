# Downloads the lexicon source data into tools/LexiconBuilder/sources.
# Requires curl.exe and tar.exe (both ship with Windows 10 1803+).
# All sources are MIT licensed; see THIRD-PARTY-NOTICES.md.

$ErrorActionPreference = 'Stop'

$dest = Join-Path $PSScriptRoot 'sources'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

function Get-File([string]$url, [string]$name) {
    $path = Join-Path $dest $name
    Write-Host "  $name"
    & curl.exe -L --fail --silent --show-error -m 120 -o $path $url
    if ($LASTEXITCODE -ne 0) {
        throw "download failed: $url"
    }

    $size = (Get-Item $path).Length
    if ($size -lt 1000) {
        throw "downloaded file looks truncated: $name ($size bytes)"
    }
}

Write-Host 'Downloading pinyin-data ...'
Get-File 'https://cdn.jsdelivr.net/gh/mozillazg/pinyin-data@master/pinyin.txt' 'pinyin-data.txt'

Write-Host 'Downloading phrase-pinyin-data ...'
Get-File 'https://cdn.jsdelivr.net/gh/mozillazg/phrase-pinyin-data@master/pinyin.txt' 'phrase-pinyin.txt'

Write-Host 'Downloading jieba dictionary ...'
Get-File 'https://cdn.jsdelivr.net/gh/fxsjy/jieba@master/jieba/dict.txt' 'jieba-dict.txt'

Write-Host 'Downloading pinyin-pro dicts ...'
$tgz = Join-Path $dest 'pinyin-pro.tgz'
Get-File 'https://registry.npmmirror.com/pinyin-pro/-/pinyin-pro-3.29.5.tgz' 'pinyin-pro.tgz'

$extract = Join-Path $dest 'pinyin-pro'
Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $extract | Out-Null
& tar.exe -xzf $tgz -C $extract 'package/dist/esm/data'

$merged = Join-Path $dest 'pinyin-pro-dicts.txt'
Remove-Item -Force $merged -ErrorAction SilentlyContinue
foreach ($n in 1..5) {
    $file = Join-Path $extract "package/dist/esm/data/dict$n.mjs"
    Get-Content -Raw -Encoding UTF8 $file | Add-Content -Encoding UTF8 $merged
}

Remove-Item -Recurse -Force $extract
Remove-Item -Force $tgz

Write-Host ''
Write-Host 'Done. Now run:'
Write-Host '  dotnet run --project tools/LexiconBuilder -c Release -- \'
Write-Host '    --pinyin-data tools/LexiconBuilder/sources/pinyin-data.txt \'
Write-Host '    --phrase-data tools/LexiconBuilder/sources/phrase-pinyin.txt \'
Write-Host '    --pinyin-pro  tools/LexiconBuilder/sources/pinyin-pro-dicts.txt \'
Write-Host '    --word-freq   tools/LexiconBuilder/sources/jieba-dict.txt \'
Write-Host '    --out         assets/lexicon.bin'
