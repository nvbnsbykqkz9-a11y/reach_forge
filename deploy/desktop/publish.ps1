<#
  Windows 版（PC 単体）を発行する。
    pwsh deploy/desktop/publish.ps1                       # artifacts/desktop/app に出力
    pwsh deploy/desktop/publish.ps1 -FfmpegDir C:\ffmpeg\bin   # ffmpeg を同梱する（動画機能用）
  出力：
    app\ReachForge.exe        画面（WPF + WebView2）
    app\server\               サーバー（ReachForge.Web）
    app\ffmpeg\ffmpeg.exe     （任意）同梱した ffmpeg
  インストーラー（Inno Setup 6）：iscc deploy/desktop/ReachForge.iss
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "artifacts/desktop",
    [string]$FfmpegDir = $env:RF_FFMPEG_DIR
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$out = Join-Path $root $Output
$app = Join-Path $out "app"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

# 実行環境（.NET）を同梱する（利用者の PC に .NET を入れなくてよい）
dotnet publish "$root/src/ReachForge.Desktop/ReachForge.Desktop.csproj" -c $Configuration -r $Runtime --self-contained -o $app
if ($LASTEXITCODE -ne 0) { throw "ReachForge.Desktop の発行に失敗しました" }
dotnet publish "$root/src/ReachForge.Web/ReachForge.Web.csproj" -c $Configuration -r $Runtime --self-contained -o (Join-Path $app "server")
if ($LASTEXITCODE -ne 0) { throw "ReachForge.Web の発行に失敗しました" }

# 開発用の設定はサーバーに含めない
Remove-Item -ErrorAction SilentlyContinue (Join-Path $app "server/appsettings.Development.json")

if ($FfmpegDir) {
    $ffmpeg = Join-Path $FfmpegDir "ffmpeg.exe"
    if (-not (Test-Path $ffmpeg)) { throw "ffmpeg.exe が見つかりません: $ffmpeg" }
    New-Item -ItemType Directory -Force (Join-Path $app "ffmpeg") | Out-Null
    Copy-Item $ffmpeg (Join-Path $app "ffmpeg/ffmpeg.exe")
}
Write-Host "発行しました: $app"
