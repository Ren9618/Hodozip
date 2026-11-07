; Hodozip installer script (Inno Setup 6).
;
; Build with:
;   pwsh -File packaging/build-installer.ps1
;
; The script installs the unpackaged, self-contained publish output per user
; (no administrator rights required). The optional shell integration uses the
; app's own HKCU registration code so that it stays in sync with the Settings
; window.

#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif
#ifndef AppVersion4
  #define AppVersion4 "0.6.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\installer\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef IconFile
  #define IconFile "..\artifacts\installer\assets\Hodozip.ico"
#endif

[Setup]
AppId={{A7C4E2B1-6D3F-4E8A-9B5C-1F2D3E4A5B6C}
AppName=Hodozip
AppVersion={#AppVersion}
AppVerName=Hodozip {#AppVersion}
AppPublisher=Ren9618
AppPublisherURL=https://github.com/Ren9618/Hodozip
AppSupportURL=https://github.com/Ren9618/Hodozip/issues
AppUpdatesURL=https://github.com/Ren9618/Hodozip/releases
AppCopyright=Copyright (c) 2026 Rayno Works (Ren9618)
DefaultDirName={localappdata}\Programs\Hodozip
DefaultGroupName=Hodozip
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Hodozip-{#AppVersion}-setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\Hodozip.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
LicenseFile=..\LICENSE
VersionInfoVersion={#AppVersion4}
VersionInfoCompany=Ren9618
VersionInfoDescription=Hodozip Setup
VersionInfoProductName=Hodozip
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.ShellIntegration=シェル統合:
english.ShellIntegration=Shell integration:
japanese.RegisterContextMenu=従来型の右クリックメニューを登録する（このユーザーのみ）
english.RegisterContextMenu=Register the classic right-click menu (current user only)
japanese.RegisterAssociations=対応アーカイブを「プログラムから開く」に追加する
english.RegisterAssociations=Add supported archives to "Open with"
japanese.ContextMenuRegisterFailed=従来型の右クリック メニューを登録できませんでした。セットアップを続行できません。%n%nHodozip を起動し、設定画面の「従来型コンテキストメニュー」から登録し直してください。
english.ContextMenuRegisterFailed=The classic right-click menu could not be registered. Setup cannot continue.%n%nStart Hodozip and register it again from "Classic context menu" in Settings.

[Tasks]
Name: "contextmenu"; Description: "{cm:RegisterContextMenu}"; GroupDescription: "{cm:ShellIntegration}"; Flags: checkedonce
Name: "associate"; Description: "{cm:RegisterAssociations}"; GroupDescription: "{cm:ShellIntegration}"; Flags: checkedonce
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\Hodozip\Assets\Hodozip.ico"; DestDir: "{app}"; DestName: "Hodozip.ico"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Hodozip"; Filename: "{app}\Hodozip.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Hodozip.ico"
Name: "{autodesktop}\Hodozip"; Filename: "{app}\Hodozip.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Hodozip.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\Hodozip.exe"; Description: "{cm:LaunchProgram,Hodozip}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Hodozip.exe"; Parameters: "--unregister-context-menu"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterContextMenu"

[Registry]
; ProgID used by the "Open with" candidates below.
Root: HKCU; Subkey: "Software\Classes\Hodozip.Archive"; ValueType: string; ValueName: ""; ValueData: "Hodozip Archive"; Flags: uninsdeletekey; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\Hodozip.Archive\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Hodozip.exe,0"; Flags: uninsdeletekey; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\Hodozip.Archive\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Hodozip.exe"" ""%1"""; Flags: uninsdeletekey; Tasks: associate

; Make Hodozip a candidate in Explorer's "Open with" menu without changing the
; user's current default application.
;
; Inno Setup's "none" value type only creates the key, so an empty string value
; is written instead. The shell keys off the value name, not the data.
Root: HKCU; Subkey: "Software\Classes\.zip\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.7z\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.rar\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.msi\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.tar\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.gz\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.tgz\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.bz2\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.tbz2\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.xz\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.txz\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.zst\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.tzst\OpenWithProgids"; ValueType: string; ValueData: ""; ValueName: "Hodozip.Archive"; Flags: uninsdeletevalue; Tasks: associate

; Allow "Hodozip" to be resolved from the Run dialog and the App Paths lookup.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Hodozip.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\Hodozip.exe"; Flags: uninsdeletekey

[Code]
var
  ContextMenuRegistrationFailed: Boolean;
  ExplorerRestartNeeded: Boolean;

// Loaded shell-extension DLLs cannot be overwritten. If the old handler is
// still loaded in Explorer, restart Explorer before copying files so the
// upgrade can install the unified handler without a reboot.
function IsFileLocked(const FileName: String): Boolean;
var
  Stream: TFileStream;
begin
  Result := False;
  if not FileExists(FileName) then
    Exit;

  try
    Stream := TFileStream.Create(FileName, fmOpenWrite or fmShareExclusive);
    Stream.Free;
  except
    Result := True;
  end;
end;

procedure RestartExplorerIfNeeded(const DllPath: String);
var
  ResultCode: Integer;
begin
  if not IsFileLocked(DllPath) then
    Exit;

  Log('Context menu DLL is locked; restarting Explorer before install.');
  if not Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im explorer.exe', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Could not run taskkill for explorer.exe.');
    Exit;
  end;

  Sleep(1200);
  ExplorerRestartNeeded := True;
end;

procedure StartExplorerIfNeeded();
var
  ResultCode: Integer;
begin
  if not ExplorerRestartNeeded then
    Exit;

  Log('Restarting Explorer.');
  Exec(ExpandConstant('{win}\explorer.exe'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  ExplorerRestartNeeded := False;
end;

const
  HodozipUnifiedClsid = '{4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03}';

function VerifyLegacyContextMenu(): Boolean;
var
  Value: String;
  DllPath: String;
begin
  Result := False;
  DllPath := ExpandConstant('{app}\Hodozip.ContextMenu.dll');
  Log('Context menu verify: expected DLL = ' + DllPath);

  // v0.6.0 の統合ハンドラー: 解凍 2 項目と圧縮を 1 つの CLSID で表示する。
  if not RegQueryStringValue(HKCU64, 'Software\Classes\CLSID\{4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03}\InprocServer32', '', Value) then
  begin
    Log('Context menu verify: unified InprocServer32 value is missing.');
    Exit;
  end;
  Log('Context menu verify: unified InprocServer32 = ' + Value);
  if CompareText(Value, DllPath) <> 0 then
  begin
    Log('Context menu verify: unified InprocServer32 does not match the installed DLL.');
    Exit;
  end;

  if not RegQueryStringValue(HKCU64, 'Software\Classes\CLSID\{4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03}\InprocServer32', 'ThreadingModel', Value) then
  begin
    Log('Context menu verify: unified ThreadingModel value is missing.');
    Exit;
  end;
  if CompareText(Value, 'Apartment') <> 0 then
  begin
    Log('Context menu verify: unified ThreadingModel is not Apartment.');
    Exit;
  end;

  if not RegQueryStringValue(HKCU64, 'Software\Classes\*\shellex\ContextMenuHandlers\Hodozip', '', Value) then
  begin
    Log('Context menu verify: * handler value is missing.');
    Exit;
  end;
  if CompareText(Value, HodozipUnifiedClsid) <> 0 then
  begin
    Log('Context menu verify: * handler is not the braced unified CLSID.');
    Exit;
  end;

  if not RegQueryStringValue(HKCU64, 'Software\Classes\Directory\shellex\ContextMenuHandlers\Hodozip', '', Value) then
  begin
    Log('Context menu verify: Directory handler value is missing.');
    Exit;
  end;
  if CompareText(Value, HodozipUnifiedClsid) <> 0 then
  begin
    Log('Context menu verify: Directory handler is not the braced unified CLSID.');
    Exit;
  end;

  // 旧レイアウトが残っていないこと（登録処理が掃除する）。
  if RegQueryStringValue(HKCU64, 'Software\Classes\SystemFileAssociations\.zip\shellex\ContextMenuHandlers\Hodozip', '', Value) then
  begin
    Log('Context menu verify: legacy per-extension handler still exists.');
    Exit;
  end;

  if RegQueryStringValue(HKCU64, 'Software\Classes\*\shellex\ContextMenuHandlers\HodozipCompress', '', Value) then
  begin
    Log('Context menu verify: legacy compress handler still exists.');
    Exit;
  end;

  Result := True;
end;

procedure FailContextMenuRegistration(Reason: String);
begin
  ContextMenuRegistrationFailed := True;
  Log('Context menu registration failed: ' + Reason);
  StartExplorerIfNeeded();
  // サイレント インストールではメッセージ ボックスが誰にも閉じられず
  // 停止してしまうため、ログだけ残して中止する。
  if not WizardSilent then
    MsgBox(ExpandConstant('{cm:ContextMenuRegisterFailed}'), mbError, MB_OK);
  Abort;
end;

procedure DeinitializeSetup();
begin
  // If setup aborted after stopping Explorer, bring the shell back.
  StartExplorerIfNeeded();
end;

function GetCustomSetupExitCode: Integer;
begin
  if ContextMenuRegistrationFailed then
    Result := 1
  else
    Result := 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    RestartExplorerIfNeeded(ExpandConstant('{app}\Hodozip.ContextMenu.dll'));
    Exit;
  end;

  if CurStep <> ssPostInstall then
    Exit;

  if not WizardIsTaskSelected('contextmenu') then
  begin
    StartExplorerIfNeeded();
    Exit;
  end;

  // 登録はアプリ本体の --register-context-menu に一本化する。
  // ここで終了コードとレジストリ内容を検証し、失敗時は無言で
  // 続行せずセットアップを中止する。
  if not Exec(ExpandConstant('{app}\Hodozip.exe'), '--register-context-menu', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    FailContextMenuRegistration('Hodozip.exe could not be started.');

  Log('--register-context-menu exit code: ' + IntToStr(ResultCode));
  if ResultCode <> 0 then
    FailContextMenuRegistration('Hodozip.exe returned exit code ' + IntToStr(ResultCode) + '.');

  if not VerifyLegacyContextMenu() then
    FailContextMenuRegistration('Registry verification failed.');

  Log('Context menu registration verified successfully.');
  StartExplorerIfNeeded();
end;
