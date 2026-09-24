; BrainX Node - Windows Server installer.
; Bundles the self-contained brainx-node, registers it as the "BrainXNode"
; Windows Service (via Install-Service.ps1 -> generates a bearer token + starts
; it on 127.0.0.1:5142), and drops Setup-Tunnel.ps1 for the Cloudflare step.
;
; publish\node must hold the same layout as the release's full node zip:
;   BrainX.Server.exe + wwwroot   the node
;   mcp\brainx-mcp.exe            BrainX Cloud /mcp sessions + re-index
;   manager\BrainX.ServerManager.exe   the control app (tray, elevated)
; publish.ps1 -Node builds exactly that.
;
; Build:  ISCC.exe /DAppVer=<ver> BrainXNode.iss
; Output: dist\BrainXNode-Setup-<ver>.exe
; Keep this file pure ASCII (see the deploy-script note in DEPLOY.md history).

#define AppName "BrainX Node"
#ifndef AppVer
  #define AppVer "2.0.136"
#endif

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
Name: "desktopicon"; Description: "Create a desktop shortcut to BrainX Server Manager"; GroupDescription: "Shortcuts:"

[Icons]
; BrainX Server Manager is the node's control app: service start/stop/restart,
; update, BrainX Cloud customers, settings, owner token, log. Its own first-run
; offer creates this same Start Menu .lnk, so there is never a duplicate.
Name: "{autoprograms}\BrainX Server Manager"; Filename: "{app}\app\manager\BrainX.ServerManager.exe"; WorkingDir: "{app}\app\manager"; Comment: "Control the BrainX Node service"
Name: "{autodesktop}\BrainX Server Manager";  Filename: "{app}\app\manager\BrainX.ServerManager.exe"; WorkingDir: "{app}\app\manager"; Tasks: desktopicon
; The web Control Panel is still there (Inno makes an internet shortcut when
; the Filename is a URL). localhost so it works before the tunnel is up.
Name: "{autoprograms}\BrainX Node Dashboard"; Filename: "http://localhost:5142/"; Comment: "Open the BrainX Node Control Panel"

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Install-Service.ps1"" -AppDir ""{app}\app"" -VaultDir ""{app}\vault"""; \
  StatusMsg: "Registering + starting the BrainX Node service..."; \
  Flags: waituntilterminated
Filename: "{app}\app\manager\BrainX.ServerManager.exe"; Description: "Open BrainX Server Manager now"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/IM BrainX.ServerManager.exe /F"; Flags: runhidden; RunOnceId: "KillMgr"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""BrainX Server Manager"" /F"; Flags: runhidden; RunOnceId: "DelMgrTask"
Filename: "sc.exe"; Parameters: "stop BrainXNode";   Flags: runhidden; RunOnceId: "StopSvc"
Filename: "sc.exe"; Parameters: "delete BrainXNode"; Flags: runhidden; RunOnceId: "DelSvc"

[Messages]
FinishedLabel=BrainX Node is running as the "BrainXNode" Windows service on 127.0.0.1:5142.%n%nControl it with BrainX Server Manager (Start Menu): start/stop/update the service, BrainX Cloud customers, settings, owner token and the log. The web Control Panel is at http://localhost:5142/.%n%nNEXT: run C:\brainx\Setup-Tunnel.ps1 -Domain <your-domain> for the Cloudflare Tunnel (skip it when the tunnel already exists).
