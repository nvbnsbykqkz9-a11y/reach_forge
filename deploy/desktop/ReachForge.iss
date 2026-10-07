; Windows 版のインストーラー（Inno Setup 6）。先に deploy/desktop/publish.ps1 で artifacts/desktop/app を作る。
;   iscc deploy/desktop/ReachForge.iss
; 管理者権限なしで利用者のフォルダー（%LOCALAPPDATA%\Programs\ReachForge）に入れる。
; データ（%LOCALAPPDATA%\ReachForge）はアンインストールしても残す（入れ直しても投稿・設定を引き継ぐ）。

#define AppName "ReachForge"
#define AppVersion "1.0.0"
#define AppExe "ReachForge.exe"

[Setup]
AppId={{6C1B7C2E-3B6F-4C34-9E0B-5F2D3E7A9B41}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ReachForge
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\..\artifacts\desktop
OutputBaseFilename=ReachForge-Setup-{#AppVersion}
SetupIconFile=..\..\src\ReachForge.Desktop\ReachForge.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 起動中なら終了してもらう（アプリの多重起動防止と同じ名前）
AppMutex=ReachForge.Desktop
CloseApplications=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにアイコンを作る"; GroupDescription: "追加のアイコン:"
Name: "autostart"; Description: "Windows の起動時にタスクトレイで開始する（予約投稿を続けるため）"; GroupDescription: "起動:"

[Files]
Source: "..\..\artifacts\desktop\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "ReachForge を起動する"; Flags: nowait postinstall skipifsilent

[Code]
// WebView2 ランタイム（Windows 11 と多くの Windows 10 には入っている）がなければ案内する
function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
  Result := Result and (Version <> '') and (Version <> '0.0.0.0');
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not WebView2Installed() then
  begin
    if MsgBox('画面の表示に使う Microsoft Edge WebView2 ランタイムが見つかりません。' + #13#10 +
              'ダウンロードページを開きますか？（インストール後にもう一度このセットアップを実行してください）',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;
