; SPDX-License-Identifier: GPL-3.0-or-later
; Copyright (C) 2026 Pablo Pramparo
;
; Instalador de Kantaria (Inno Setup 6). Se invoca desde scripts\publish.ps1 -Installer, que pasa:
;   /DAppVersion=<versión>  /DSourceDir=<carpeta publicada>  /DOutputDir=<carpeta de salida>  /DRepoDir=<raíz del repo>
; Instala por usuario (sin administrador) en %LOCALAPPDATA%\Programs\Kantaria. Los datos del usuario viven en
; %APPDATA%\Kantor y no se tocan al actualizar ni al desinstalar.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error Falta /DSourceDir (usar scripts\publish.ps1 -Installer)
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef RepoDir
  #define RepoDir ".."
#endif

[Setup]
AppId={{6B8E5F52-3C1B-4D3A-9A57-2B6E0C4F7A11}
AppName=Kantaria
AppVersion={#AppVersion}
AppVerName=Kantaria {#AppVersion}
AppPublisher=Pablo Pramparo
AppCopyright=Copyright (C) 2026 Pablo Pramparo
DefaultDirName={autopf}\Kantaria
DefaultGroupName=Kantaria
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Kantaria-Setup-{#AppVersion}
SetupIconFile={#RepoDir}\src\Kantor.App\Assets\Branding\kantaria.ico
UninstallDisplayIcon={app}\Kantor.App.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Una versión nueva no debe heredar archivos de la anterior (plugins de LibVLC, bibliotecas que ya no existen).
Type: filesandordirs; Name: "{app}\libvlc"
Type: files; Name: "{app}\*.dll"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Kantaria"; Filename: "{app}\Kantor.App.exe"
Name: "{autodesktop}\Kantaria"; Filename: "{app}\Kantor.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Kantor.App.exe"; Description: "{cm:LaunchProgram,Kantaria}"; Flags: nowait postinstall skipifsilent
