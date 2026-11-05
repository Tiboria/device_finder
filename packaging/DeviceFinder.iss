; Device Finder — installeur hors-ligne (Inno Setup 6), multilingue FR/EN
; Génère publish\DeviceFinder-Setup.exe via packaging\build-release.ps1 (ou ISCC.exe).
; La page de choix de langue est présélectionnée sur la langue de l'OS ; tout est
; embarqué, aucun téléchargement.

#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

#define AppName "Device Finder"
#define AppExe "DeviceFinder.exe"
#define NpcapInstaller "npcap-1.88.exe"
; Chemins relatifs au dossier packaging\, indépendants de la machine de build.
#define PublishDir "..\publish\DeviceFinder-win-x64"
#define NpcapSource "..\src\DeviceFinder.App\Resources\" + NpcapInstaller
#define AppIcon "..\src\DeviceFinder.App\Resources\app.ico"

[Setup]
AppId={{7F4C0F5B-4A0E-4C3B-9C1D-52B5D21E4A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Tiboria
; Pas de mise à jour en ligne, pas de contact réseau
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile={#AppIcon}
DefaultDirName={autopf}\DeviceFinder
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}
OutputDir=..\publish
OutputBaseFilename=DeviceFinder-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10/11 uniquement
MinVersion=10.0

[Languages]
; Le français est présélectionné sur un OS français (détection locale Inno).
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; FR
fr.NpcapTask=Installer Npcap (détection ARP des équipements en IP fixe) — recommandé
fr.DetectionGroup=Composants de détection :
fr.ShortcutsGroup=Raccourcis :
fr.DesktopTask=Créer un raccourci sur le bureau
fr.UninstallShortcut=Désinstaller {#AppName}
fr.NpcapRunDesc=Installer Npcap
; EN
en.NpcapTask=Install Npcap (ARP detection of fixed-IP devices) — recommended
en.DetectionGroup=Detection components:
en.ShortcutsGroup=Shortcuts:
en.DesktopTask=Create a desktop shortcut
en.UninstallShortcut=Uninstall {#AppName}
en.NpcapRunDesc=Install Npcap

[Tasks]
Name: "npcap"; Description: "{cm:NpcapTask}"; GroupDescription: "{cm:DetectionGroup}"
Name: "desktopicon"; Description: "{cm:DesktopTask}"; GroupDescription: "{cm:ShortcutsGroup}"; Flags: checkedonce

[Files]
; Application self-contained : le runtime .NET 8 est embarqué, aucun prérequis.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Installeur Npcap embarqué dans le Setup.exe, supprimé du dossier temporaire après usage.
Source: "{#NpcapSource}"; DestDir: "{tmp}"; Flags: deleteafterinstall ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Npcap : assistant GUI (mode accepté), lancé à la fin de l'installation si la case est cochée.
; Npcap demande lui-même l'élévation ; l'installeur tourne déjà en admin.
Filename: "{tmp}\{#NpcapInstaller}"; Description: "{cm:NpcapRunDesc}"; Tasks: npcap; Flags: skipifsilent nowait
