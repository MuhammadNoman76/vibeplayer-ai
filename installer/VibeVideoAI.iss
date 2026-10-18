; VibeVideo AI installer (Inno Setup 6): VibePlayer with an on-device scene filter.
; Built by build-ai.ps1, which publishes each edition to ..\publish\vibevideo-ai-<edition> and generates associations.iss.
;   /DEdition=x64     Windows 10 (1903+) / 11, 64-bit Intel/AMD
;   /DEdition=arm64   Windows 10 (1903+) / 11 on ARM
; A separate app from VibePlayer (its own AppId, folder, settings and update feed): both can be installed.

#define AppName "VibeVideo AI"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Edition
  #define Edition "x64"
#endif
#define AppExe "VibeVideoAI.exe"
#define ProgId "VibeVideoAI.Media"
#define PublishDir "..\publish\vibevideo-ai-" + Edition

[Setup]
; Same AppId for both editions (and never VibePlayer's): another edition simply replaces the current one.
AppId={{B7F3D2A1-6C4E-4F8B-9A15-3E2D7C9F0B64}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} preview
AppPublisher=Muhammad Noman
AppPublisherURL=https://www.nomanshafiq.com/vibevideo-ai/
AppComments=VibePlayer with an on-device filter that skips nudity, sex and kissing scenes
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=VibeVideoAI-Setup-{#AppVersion}-{#Edition}
SetupIconFile=..\src\VibePlayer\Assets\VibePlayer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
; The app is larger than VibePlayer (Windows ML): compress in the 64-bit helper, the 32-bit compiler runs out of memory.
LZMAUseSeparateProcess=yes
; Windows ML (the AI runtime) needs Windows 10 version 1903 or newer.
MinVersion=10.0.18362
#if Edition == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif
; "Install for all users" by default; "only for me" needs no admin rights.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
; Keep exact file times: VLC's plugin cache (plugins.dat) is only valid if plugin timestamps are unchanged.
TimeStampRounding=0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "fileassoc"; Description: "Add {#AppName} to ""Open with"" for video and audio files"; GroupDescription: "Windows integration:"
Name: "foldermenu"; Description: "Add ""Play with {#AppName}"" to the folder right-click menu"; GroupDescription: "Windows integration:"

[InstallDelete]
; Switching editions: remove the VLC engine the previous edition used and this one doesn't.
#if Edition == "x64"
Type: filesandordirs; Name: "{app}\libvlc\win-arm64"
#else
Type: filesandordirs; Name: "{app}\libvlc\win-x64"
#endif
; Updating: remove the previous version's program files first, so nothing obsolete is left behind.
; Only in the app's own folder. Settings (%APPDATA%\VibeVideoAI) and the uninstaller (unins000.*) are never touched.
Type: filesandordirs; Name: "{app}\libvlc"; Check: IsOwnFolder
Type: files; Name: "{app}\*.dll"; Check: IsOwnFolder
Type: files; Name: "{app}\*.json"; Check: IsOwnFolder
Type: files; Name: "{app}\*.config"; Check: IsOwnFolder
Type: files; Name: "{app}\*.pdb"; Check: IsOwnFolder
Type: files; Name: "{app}\createdump.exe"; Check: IsOwnFolder
Type: files; Name: "{app}\{#AppExe}"; Check: IsOwnFolder

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; ---- ProgID used by every associated extension
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "Media file ({#AppName})"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Media file ({#AppName})"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell"; ValueType: string; ValueName: ""; ValueData: "open"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc

; ---- Application registration (shows up in "Open with" / "Choose another app")
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

; ---- Default Apps (Settings > Apps > Default apps > VibeVideo AI)
Root: HKA; Subkey: "Software\{#AppName}"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Plays almost any video or audio file and skips the scenes its on-device filter finds."; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue; Tasks: fileassoc

; ---- "Play with VibeVideo AI" on folders
Root: HKA; Subkey: "Software\Classes\Directory\shell\VibeVideoAI.Play"; ValueType: string; ValueName: ""; ValueData: "Play with {#AppName}"; Flags: uninsdeletekey; Tasks: foldermenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\VibeVideoAI.Play"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: foldermenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\VibeVideoAI.Play\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: foldermenu

; ---- Per-extension entries (generated by build-ai.ps1 from MediaFormats.cs)
#include "associations.iss"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; The app's updater runs setup with /VERYSILENT ... /RELAUNCH=1: start it again afterwards
; (as the signed-in user, not elevated, even when setup asked for administrator rights).
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[UninstallDelete]
; VLC's plugin cache (shipped for x64; the app may create it itself on first run)
Type: files; Name: "{app}\libvlc\win-x64\plugins\plugins.dat"
Type: files; Name: "{app}\libvlc\win-arm64\plugins\plugins.dat"
; The downloaded AI models, scan results and NPU compile cache (hundreds of MB; downloaded again if reinstalled).
; Settings and skips in %APPDATA%\VibeVideoAI stay, like VibePlayer's.
Type: filesandordirs; Name: "{localappdata}\VibeVideoAI"

[Code]
// /RELAUNCH=1 on the command line (passed by the app's updater).
function RelaunchRequested(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

// The install folder is the app's own (named "VibeVideo AI" and holding VibeVideoAI.exe), so old program files can go.
function IsOwnFolder(): Boolean;
begin
  Result := (CompareText(ExtractFileName(RemoveBackslashUnlessRoot(ExpandConstant('{app}'))), '{#AppName}') = 0) and
            FileExists(ExpandConstant('{app}\{#AppExe}'));
end;
