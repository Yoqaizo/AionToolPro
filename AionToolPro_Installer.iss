; Inno Setup 6 & 7 Script for AionToolPro
#ifndef AppVer
  #define AppVer "0.9.2"
#endif

[Setup]
AppId={{5D2B9F4E-A341-4B7C-9C1D-8F6E2A5D3B2C}
AppName=AionToolPro
AppVersion={#AppVer}
AppVerName=AionToolPro {#AppVer}
AppPublisher=Yoqaizo
AppPublisherURL=https://github.com/Yoqaizo/AionToolPro
AppSupportURL=https://github.com/Yoqaizo/AionToolPro/issues
AppUpdatesURL=https://github.com/Yoqaizo/AionToolPro/releases
DefaultDirName={autopf}\AionToolPro
DefaultGroupName=AionToolPro
LicenseFile=LICENSE
OutputDir=.\Installer
OutputBaseFilename=AionToolPro_Setup_v{#AppVer}
SetupIconFile=AionToolPro.Desktop\AionToolp.ico
UninstallDisplayIcon={app}\AionToolPro.exe
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
UsedUserAreasWarning=no
#if VER >= 0x06030000
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
#endif
CloseApplications=force
RestartApplications=no

[Files]
Source: "AionToolPro.Desktop\bin\Release\net8.0-windows\win-x64\publish\AionToolPro.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "AionToolPro.Desktop\bin\Release\net8.0-windows\win-x64\publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "AionToolPro.Desktop\AionToolp.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\AionToolPro"; Filename: "{app}\AionToolPro.exe"; WorkingDir: "{app}"; IconFilename: "{app}\AionToolp.ico"
Name: "{commondesktop}\AionToolPro"; Filename: "{app}\AionToolPro.exe"; WorkingDir: "{app}"; IconFilename: "{app}\AionToolp.ico"

[Run]
; shellexec avoids CreateProcess error 740 on elevated requireAdministrator manifests
Filename: "{app}\AionToolPro.exe"; WorkingDir: "{app}"; Description: "Launch AionToolPro"; Flags: nowait postinstall runascurrentuser shellexec

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""AionToolPro_AutoStart"" /F"; Flags: runhidden; RunOnceId: "RemoveAionToolProAutoStart"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\AionToolPro"
Type: filesandordirs; Name: "{commonappdata}\AionToolPro"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM AionToolPro.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;
