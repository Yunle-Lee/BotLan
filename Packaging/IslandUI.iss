#ifndef Payload
  #error Payload must name the published application directory
#endif
#ifndef Bootstrapper
  #error Bootstrapper must name Microsoft's signed WebView2 installer
#endif
#ifndef Output
  #define Output "."
#endif
[Setup]
AppId={{823C74D2-73CD-4FCB-84AA-C9C2F88F1358}
AppName=IslandUI
AppVersion=1.1.0
DefaultDirName={localappdata}\Programs\IslandUI
DefaultGroupName=IslandUI
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#Output}
OutputBaseFilename=IslandUI-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\IslandUI.exe
Uninstallable=not IsPortable
CreateUninstallRegKey=not IsPortable
SetupLogging=yes

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Bootstrapper}"; Flags: dontcopy

[Icons]
Name: "{userdesktop}\IslandUI"; Filename: "{app}\IslandUI.exe"; WorkingDir: "{app}"; Check: not IsPortable
Name: "{userprograms}\IslandUI\IslandUI"; Filename: "{app}\IslandUI.exe"; WorkingDir: "{app}"; Check: not IsPortable
Name: "{userprograms}\IslandUI\卸载 IslandUI"; Filename: "{uninstallexe}"; Check: not IsPortable

[Run]
Filename: "{app}\IslandUI.exe"; WorkingDir: "{app}"; Flags: nowait skipifsilent; Check: not IsPortable

[Code]
function IsPortable: Boolean;
begin
  Result := ExpandConstant('{param:PORTABLE|0}') = '1';
end;

function HasWebView: Boolean;
var
  Version: String;
begin
  Result := (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
    and (Version <> '') and (Version <> '0.0.0.0'));
  if not Result then
    Result := (RegQueryStringValue(HKLM32, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
      and (Version <> '') and (Version <> '0.0.0.0'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if IsPortable or HasWebView then Exit;
  WizardForm.StatusLabel.Caption := '正在安装微软 WebView2 组件（需要网络）…';
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) or not HasWebView then
    Result := 'WebView2 组件尚未安装。请检查网络，安装微软 WebView2 Runtime 后重新运行安装程序。';
end;
