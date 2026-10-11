; phosphor.iss - one-click installer for PHOSPHOR (Inno Setup 6). Built by scripts\package.ps1, not by hand:
;   ISCC /DPkgDir=<dist\PHOSPHOR-x.y.z-hash> /DAppVer=x.y.z /DOutName=PHOSPHOR-Setup-x.y.z /DOutDir=<dist> phosphor.iss
; No wizard pages except the last one. Per-user, no admin rights. Files and registry are done by the same
; install.ps1 / uninstall.ps1 as Установить.bat / Удалить.bat, so both ways install identically.
; The .scr file association is NOT touched: Windows starts the screensaver by the path in SCRNSAVE.EXE.

#ifndef PkgDir
  #error Pass /DPkgDir=<package folder>
#endif
#ifndef AppVer
  #define AppVer "0.0.0"
#endif
#ifndef OutName
  #define OutName "PHOSPHOR-Setup"
#endif
#ifndef OutDir
  #define OutDir "."
#endif

[Setup]
AppId={{6E1B7C52-3F0A-4C1E-9B8D-50F05F0A7D21}
AppName=PHOSPHOR
AppVersion={#AppVer}
AppVerName=PHOSPHOR {#AppVer}
AppPublisher=k0rry
AppPublisherURL=https://github.com/k0rruptrr/phosphor
DefaultDirName={localappdata}\Programs\Phosphor
PrivilegesRequired=lowest
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
CreateUninstallRegKey=yes
UninstallDisplayName=PHOSPHOR (заставка)
UninstallDisplayIcon={app}\Phosphor.scr
SetupIconFile=..\..\assets\logo\phosphor.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutDir}
OutputBaseFilename={#OutName}

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
ru.FinishedHeadingLabel=PHOSPHOR установлен
ru.FinishedLabelNoIcons=Заставка включена. Настройки — в «Параметрах заставки» → PHOSPHOR → «Параметры».

[Files]
; The package goes to a temp folder; install.ps1 copies it to {app} (keeping the user's phosphor.json).
Source: "{#PkgDir}\*"; DestDir: "{tmp}\pkg"; Flags: recursesubdirs ignoreversion
; uninstall.ps1 from scripts\ (has -RegistryOnly); the folder itself is removed by [UninstallDelete].
Source: "..\uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "control.exe"; Parameters: "desk.cpl,,@screensaver"; Description: "Открыть параметры заставки"; Flags: postinstall nowait skipifsilent

[Code]
var
  Fresh: Boolean;

function PowerShell(const Args: String; var Code: Integer): Boolean;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass ' + Args, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('powershell ' + Args + ' -> started=' + IntToStr(Ord(Result)) + ' exit=' + IntToStr(Code));
end;

function InitializeSetup(): Boolean;
begin
  // Upgrade keeps the user's screensaver timeout; a fresh install sets 5 minutes.
  Fresh := not FileExists(ExpandConstant('{localappdata}\Programs\Phosphor\Phosphor.scr'));
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Minutes: String;
begin
  if CurStep = ssPostInstall then
  begin
    if Fresh then Minutes := '5' else Minutes := '0';
    if not PowerShell('-File "' + ExpandConstant('{tmp}\pkg\install.ps1') + '" -Source "' +
        ExpandConstant('{tmp}\pkg') + '" -TimeoutMinutes ' + Minutes, Code) or (Code <> 0) then
      MsgBox('Не удалось прописать заставку (код ' + IntToStr(Code) + '). ' +
        'Можно поставить вручную: распакуй zip-релиз и запусти Установить.bat.', mbError, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Script: String;
begin
  // Restores the previous screensaver; the folder (with phosphor.json) goes via [UninstallDelete].
  // -Command, not -File: with -File "-Confirm:$false" arrives as a string and the script refuses to start.
  if CurUninstallStep = usUninstall then
  begin
    Script := ExpandConstant('{app}\uninstall.ps1');
    StringChangeEx(Script, '''', '''''', True);
    PowerShell('-Command "& ''' + Script + ''' -RegistryOnly -Confirm:$false; exit $LASTEXITCODE"', Code);
  end;
end;
