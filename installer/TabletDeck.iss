; Tablet Deck installer, for Inno Setup 6. Build it with `npm run build`
; (scripts/build.js), which passes AppVersion, FileVersion and BuildDir.

#ifndef AppVersion
  #error Build the installer with "npm run build", not by compiling this file directly.
#endif

#define AppName "Tablet Deck"
; The app window (native/launcher.cs), and the server it runs hidden (server.js).
#define AppExe "TabletDeck.exe"
#define ServerExe "TabletDeckServer.exe"
#define RepoUrl "https://github.com/nodieCode/tablet-deck"
#define FirewallRule "Tablet Deck"

[Setup]
; Never change AppId: Windows uses it to recognise upgrades of the same app.
AppId={{A7A9440D-F358-4D02-81DE-04193AAA6A52}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=nodieCode
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#FileVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright=Copyright (c) 2026 nodieCode. MIT License.
SourceDir=..
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
; Program Files and the firewall rule need administrator rights.
PrivilegesRequired=admin
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=TabletDeck-Setup-{#AppVersion}
SetupIconFile={#BuildDir}\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
RestartApplications=no
; ---- Code signing ----
; scripts/build.js writes build\sign.iss (SignTool=... and SignedUninstaller=yes) only
; when SIGN_CMD is set, so unsigned builds skip this. See .github/workflows/release.yml.
#if FileExists(BuildDir + "\sign.iss")
  #include BuildDir + "\sign.iss"
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nTablet Deck turns a tablet or phone on your home network into a touch control panel for Star Citizen.%n%nFan project. Not affiliated with or endorsed by Cloud Imperium Games.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "scprofile"; Description: "Install the Star Citizen keybind profile (Tablet Deck Keyboard)"; GroupDescription: "Star Citizen:"

[Files]
Source: "{#BuildDir}\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\app\{#ServerExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\app\native\*.exe"; DestDir: "{app}\native"; Flags: ignoreversion
Source: "keybinds\TabletDeck-StarCitizen.xml"; DestDir: "{app}\keybinds"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
; The copy in the game folder stays on uninstall: the player may have it loaded in-game.
Source: "keybinds\TabletDeck-StarCitizen.xml"; DestDir: "{code:MappingsDir}"; Tasks: scprofile; Check: HasStarCitizen; Flags: ignoreversion uninsneveruninstall

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Let tablets reach the server on private (home) networks only, so Windows doesn't ask.
; Deleting first keeps reinstalls and upgrades from adding a second rule.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\{#ServerExe}"" enable=yes profile=private"; Flags: runhidden; StatusMsg: "Allowing Tablet Deck through Windows Firewall on private networks..."
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Registry]
; The window's "Start with Windows" checkbox writes this value; uninstalling removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Tablet Deck"; Flags: uninsdeletevalue dontcreatekey

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[Code]
var
  StarCitizenDir: String; // ...\StarCitizen\LIVE, or '' when not found
  TasksLabelled: Boolean;

// The same install folders server.js searches for actionmaps.xml.
function FindStarCitizen(): String;
var
  Drive, I: Integer;
  Roots: array[0..3] of String;
begin
  Result := '';
  Roots[0] := 'Program Files\Roberts Space Industries';
  Roots[1] := 'Roberts Space Industries';
  Roots[2] := 'RSI';
  Roots[3] := 'Games\Roberts Space Industries';
  for Drive := Ord('C') to Ord('H') do
    for I := 0 to 3 do
      if DirExists(Chr(Drive) + ':\' + Roots[I] + '\StarCitizen\LIVE') then
      begin
        Result := Chr(Drive) + ':\' + Roots[I] + '\StarCitizen\LIVE';
        Exit;
      end;
end;

function InitializeSetup(): Boolean;
begin
  StarCitizenDir := FindStarCitizen();
  Result := True;
end;

function HasStarCitizen(): Boolean;
begin
  Result := StarCitizenDir <> '';
end;

function MappingsDir(Param: String): String;
begin
  if StarCitizenDir <> '' then
    Result := StarCitizenDir + '\user\client\0\Controls\Mappings'
  else
    Result := ExpandConstant('{app}\keybinds');
end;

// Shows where the keybind profile goes, or greys the option out without Star Citizen.
// Runs once: the page can be shown again with Back.
procedure CurPageChanged(CurPageID: Integer);
var
  I: Integer;
  Caption: String;
begin
  if (CurPageID <> wpSelectTasks) or TasksLabelled then Exit;
  TasksLabelled := True;
  for I := 0 to WizardForm.TasksList.Items.Count - 1 do
  begin
    Caption := WizardForm.TasksList.ItemCaption[I];
    if Pos('keybind profile', Caption) > 0 then
    begin
      if StarCitizenDir = '' then
      begin
        WizardForm.TasksList.Checked[I] := False;
        WizardForm.TasksList.ItemEnabled[I] := False;
        WizardForm.TasksList.ItemCaption[I] := Caption + ' (Star Citizen not found)';
      end else
        WizardForm.TasksList.ItemCaption[I] := Caption + ' (into ' + StarCitizenDir + ')';
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Settings: String;
  Code: Integer;
begin
  // Close a running copy (the window, the server, and the key sender and voice listener
  // it started) so its files can be removed.
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /t /im {#ServerExe} /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(500);
  end;
  // Settings stay unless the player says otherwise; silent uninstalls always keep them.
  if CurUninstallStep = usPostUninstall then
  begin
    Settings := ExpandConstant('{userappdata}\TabletDeck');
    if DirExists(Settings) and not UninstallSilent and
       (MsgBox('Also delete your Tablet Deck settings (pairing code, port and any layouts you added)?' + #13#10#13#10 + Settings,
               mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(Settings, True, True, True);
  end;
end;
