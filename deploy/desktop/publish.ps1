<#
  Windows 版（PC 単体）を発行する。
    pwsh deploy/desktop/publish.ps1                            # artifacts/desktop/app に出力（ffmpeg を自動で取得して同梱）
    pwsh deploy/desktop/publish.ps1 -FfmpegDir C:\ffmpeg\bin   # 手元の ffmpeg を同梱する
    pwsh deploy/desktop/publish.ps1 -NoFfmpeg                  # ffmpeg を同梱しない（動画機能は PC の ffmpeg を使う）
  出力：
    app\ReachForge.exe        アプリ本体（画面と処理を1つのプロセスで動かす）
    app\wwwroot\              画面の静的ファイル
    app\ffmpeg\ffmpeg.exe     動画の書き出しに使う ffmpeg（gyan.dev の essentials ビルド。libx264 を含む GPL v3 版）
    app\ffmpeg\LICENSE.txt    ffmpeg のライセンス（GPL v3）、README.txt（ビルドの構成）、SOURCE.txt（ソースコードの入手先）
    ffmpeg-source\            同梱した ffmpeg のソースコード（GPL の求めに応じて提供できるよう、配布物と一緒に保管する）
  インストーラー（Inno Setup 6）：iscc deploy/desktop/ReachForge.iss
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "artifacts/desktop",
    [string]$FfmpegDir = $env:RF_FFMPEG_DIR,
    [switch]$NoFfmpeg,
    # 同梱する ffmpeg（版を上げるときは、ここの3つとリリースの SHA-256 をそろえて変える）
    [string]$FfmpegVersion = "8.1.2",
    [string]$FfmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip",
    [string]$FfmpegSha256 = "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec",
    [string]$FfmpegSourceUrl = "https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n8.1.2.tar.gz"
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$out = Join-Path $root $Output
$app = Join-Path $out "app"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

# 実行環境（.NET）を同梱する（利用者の PC に .NET を入れなくてよい）。画面の静的ファイルも発行の中で入る（WebAssets.targets）
dotnet publish "$root/src/ReachForge.Desktop/ReachForge.Desktop.csproj" -c $Configuration -r $Runtime --self-contained -o $app
if ($LASTEXITCODE -ne 0) { throw "ReachForge.Desktop の発行に失敗しました" }
if (-not (Test-Path (Join-Path $app "wwwroot/_framework/blazor.web.js"))) { throw "画面の静的ファイルが発行されていません" }

$ffmpegTarget = Join-Path $app "ffmpeg"
if ($FfmpegDir) {
    # 手元の ffmpeg を使う
    $ffmpeg = Join-Path $FfmpegDir "ffmpeg.exe"
    if (-not (Test-Path $ffmpeg)) { throw "ffmpeg.exe が見つかりません: $ffmpeg" }
    New-Item -ItemType Directory -Force $ffmpegTarget | Out-Null
    Copy-Item $ffmpeg (Join-Path $ffmpegTarget "ffmpeg.exe")
}
elseif (-not $NoFfmpeg) {
    # 決めた版の ffmpeg を取得し、改ざんされていないことを SHA-256 で確かめてから同梱する（取得したものは次回のために残す）
    $cache = Join-Path $root "artifacts/cache"
    New-Item -ItemType Directory -Force $cache | Out-Null
    $zip = Join-Path $cache (Split-Path $FfmpegUrl -Leaf)
    if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $FfmpegSha256.ToUpperInvariant()) {
        Write-Host "ffmpeg $FfmpegVersion を取得しています: $FfmpegUrl"
        Invoke-WebRequest -Uri $FfmpegUrl -OutFile $zip -UseBasicParsing
    }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $FfmpegSha256.ToUpperInvariant()) {
        Remove-Item $zip -Force
        throw "ffmpeg の SHA-256 が一致しません（期待 $FfmpegSha256 / 実際 $hash）。URL と SHA-256 を確認してください。"
    }
    $extract = Join-Path $cache "ffmpeg-$FfmpegVersion"
    if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
    Expand-Archive -Path $zip -DestinationPath $extract
    $build = Get-ChildItem $extract -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force $ffmpegTarget | Out-Null
    Copy-Item (Join-Path $build.FullName "bin/ffmpeg.exe") (Join-Path $ffmpegTarget "ffmpeg.exe")
    Copy-Item (Join-Path $build.FullName "LICENSE") (Join-Path $ffmpegTarget "LICENSE.txt")
    Copy-Item (Join-Path $build.FullName "README.txt") (Join-Path $ffmpegTarget "README.txt")
    @"
ReachForge に同梱している ffmpeg について

ReachForge は、動画の書き出しに ffmpeg（$FfmpegVersion、gyan.dev の essentials ビルド）を別のプログラムとして呼び出して使っています。
ffmpeg は GNU General Public License v3 の条件で配布されています（ライセンスの全文は LICENSE.txt、ビルドの構成は README.txt）。

ソースコードの入手先
  ffmpeg 本体     : $FfmpegSourceUrl
  このビルド       : https://www.gyan.dev/ffmpeg/builds/ （$FfmpegUrl）
  含まれるライブラリ: README.txt の「External libraries」に記載（x264 など。各プロジェクトのサイトで公開）

ソースコードの写しが必要な場合は、配布元（株式会社TechnologyFrontier　info@technologyfrontier.co.jp）にお問い合わせください。
"@ | Set-Content -Path (Join-Path $ffmpegTarget "SOURCE.txt") -Encoding utf8

    # GPL の求めに応じて提供できるよう、ffmpeg 本体のソースコードを配布物と一緒に保管する（インストーラーには入れない）
    $sourceDir = Join-Path $out "ffmpeg-source"
    New-Item -ItemType Directory -Force $sourceDir | Out-Null
    try {
        Invoke-WebRequest -Uri $FfmpegSourceUrl -OutFile (Join-Path $sourceDir "ffmpeg-$FfmpegVersion.tar.gz") -UseBasicParsing
    }
    catch {
        Write-Warning "ffmpeg のソースコードを取得できませんでした（$($_.Exception.Message)）。$FfmpegSourceUrl から手動で保管してください。"
    }
}

if (Test-Path (Join-Path $ffmpegTarget "ffmpeg.exe")) {
    # 動画の書き出しに使う H.264（libx264）に対応しているかを確かめる
    $encoders = & (Join-Path $ffmpegTarget "ffmpeg.exe") -hide_banner -encoders 2>&1 | Out-String
    if ($encoders -notmatch "libx264") { throw "同梱する ffmpeg が H.264（libx264）に対応していません。GPL 版（essentials / full）を使ってください。" }
    Write-Host "ffmpeg を同梱しました: $ffmpegTarget"
}
else {
    Write-Warning "ffmpeg を同梱していません。動画機能を使うには、PC に ffmpeg を入れて PATH を通す必要があります。"
}
Write-Host "発行しました: $app"
