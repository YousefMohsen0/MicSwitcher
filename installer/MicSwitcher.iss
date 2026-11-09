; Inno Setup script. Built by CI:  iscc /DAppVersion=0.3.0 /DSourceDir=publish installer\MicSwitcher.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6F0C5B1E-3C0B-4E34-9B7A-5D3A8C2E71A4}
AppName=MicSwitcher
AppVersion={#AppVersion}
AppPublisher=Yousef Mohsen
AppPublisherURL=https://github.com/YousefMohsen0/MicSwitcher
DefaultDirName={autopf}\MicSwitcher
DefaultGroupName=MicSwitcher
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=MicSwitcher-Setup-{#AppVersion}
SetupIconFile=..\src\MicSwitcher.App\Assets\app.ico
UninstallDisplayIcon={app}\MicSwitcher.exe
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MicSwitcher"; Filename: "{app}\MicSwitcher.exe"
Name: "{autodesktop}\MicSwitcher"; Filename: "{app}\MicSwitcher.exe"; Tasks: desktopicon

[Tasks]
Name: "startup"; Description: "Start MicSwitcher with Windows (runs in the background with administrator rights; asks for permission once)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Run]
; Register the startup task (shows one administrator prompt), then offer to open the app.
Filename: "{app}\MicSwitcher.exe"; Parameters: "--enable-startup"; Tasks: startup; Flags: waituntilterminated skipifsilent
; After a silent auto-update (/RELAUNCH=1) the app is started again; after a normal install the user is asked.
Filename: "{app}\MicSwitcher.exe"; Description: "Open MicSwitcher now"; Flags: nowait postinstall skipifsilent
Filename: "{app}\MicSwitcher.exe"; Flags: nowait; Check: ShouldRelaunch

[UninstallRun]
; Remove the "start with Windows" task if it was created (ignored when absent or not permitted).
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""MicSwitcher"" /F"; Flags: runhidden; RunOnceId: "RemoveStartupTask"

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
