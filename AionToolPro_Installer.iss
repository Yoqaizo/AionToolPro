; Inno Setup Script for AionToolPro 0.5
; Step 1: Run "Publish-Standalone-Exe.bat"
; Step 2: Open this file in Inno Setup and press F9 to generate AionToolPro_Setup.exe

[Setup]
AppName=AionToolPro
AppVersion=0.5
AppPublisher=TheYoqaizo
DefaultDirName={autopf}\AionToolPro
DefaultGroupName=AionToolPro
OutputDir=.\Installer
OutputBaseFilename=AionToolPro_Setup
SetupIconFile=AionToolPro.Desktop\AionToolp.ico
UninstallDisplayIcon={app}\AionToolPro.exe
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "AionToolPro.Desktop\bin\Release\net8.0-windows\win-x64\publish\AionToolPro.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "AionToolPro.Desktop\AionToolp.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\AionToolPro"; Filename: "{app}\AionToolPro.exe"; IconFilename: "{app}\AionToolp.ico"
Name: "{commondesktop}\AionToolPro"; Filename: "{app}\AionToolPro.exe"; IconFilename: "{app}\AionToolp.ico"

[Run]
Filename: "{app}\AionToolPro.exe"; Description: "Launch AionToolPro"; Flags: nowait postinstall skipifsilent
