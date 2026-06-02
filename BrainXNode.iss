; BrainX Node - Windows Server installer.
; Bundles the self-contained brainx-node, registers it as the "BrainXNode"
; Windows Service (via Install-Service.ps1 -> generates a bearer token + starts
; it on 127.0.0.1:5142), and drops Setup-Tunnel.ps1 for the Cloudflare step.
;
; Build:  ISCC.exe BrainXNode.iss   (after publishing to publish\node)
; Output: dist\BrainXNode-Setup-<ver>.exe

#define AppName "BrainX Node"
#define AppVer  "2.0.136"

[Setup]
AppId={{8F3A2C71-4B9D-4E62-A1F5-9C7D6E0B2A34}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher=xman
DefaultDirName=C:\brainx
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=dist
OutputBaseFilename=BrainXNode-Setup-{#AppVer}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}

[Files]
Source: "publish\node\*"; DestDir: "{app}\app"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "BrainX.Server\deploy\Install-Service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "BrainX.Server\deploy\Setup-Tunnel.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{app}\vault"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut to the dashboard"; GroupDescription: "Shortcuts:"

[Icons]
; The node is a headless service - its UI is the web Control Panel. These
; shortcuts open it in the default browser (Inno makes an internet shortcut
; when the Filename is a URL). localhost so it works before the tunnel is up.
Name: "{autoprograms}\BrainX Node Dashboard"; Filename: "http://localhost:5142/"; Comment: "Open the BrainX Node Control Panel"
Name: "{autodesktop}\BrainX Node Dashboard";  Filename: "http://localhost:5142/"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Install-Service.ps1"" -AppDir ""{app}\app"" -VaultDir ""{app}\vault"""; \
  StatusMsg: "Registering + starting the BrainX Node service..."; \
  Flags: waituntilterminated
Filename: "http://localhost:5142/"; Description: "Open the BrainX Node dashboard now"; \
  Flags: postinstall shellexec skipifsilent

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop BrainXNode";   Flags: runhidden; RunOnceId: "StopSvc"
Filename: "sc.exe"; Parameters: "delete BrainXNode"; Flags: runhidden; RunOnceId: "DelSvc"

[Messages]
FinishedLabel=BrainX Node is running as the "BrainXNode" Windows service (headless - there is no app window). Its UI is the web Control Panel: use the new "BrainX Node Dashboard" shortcut (Start Menu / Desktop), or open http://localhost:5142/ in a browser.%n%nBearer token: C:\brainx\bearer-token.txt (the client needs it for write endpoints).%n%nNEXT:  1) copy your vault's .obsidianx into C:\brainx\vault   2) run C:\brainx\Setup-Tunnel.ps1 -Domain <your-domain> for the Cloudflare Tunnel.
