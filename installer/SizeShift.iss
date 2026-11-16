; SizeShift - Installer (Inno Setup 6)
; Gebaut wird er mit installer\build.ps1: das veroeffentlicht zuerst die
; einzelne SizeShift.exe und ruft dann den Inno-Setup-Compiler auf.
;
; Installiert pro Benutzer (keine Administratorrechte noetig) nach
; %LOCALAPPDATA%\Programs\SizeShift - derselbe Ort wie die bisherige
; Hand-Installation, die damit sauber ersetzt wird.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\artifacts\publish\SizeShift.exe"
#endif

[Setup]
AppId={{6A3F2C1E-5B7D-4E9A-9C21-7D4B8E0F1A52}
AppName=SizeShift
AppVersion={#AppVersion}
AppVerName=SizeShift {#AppVersion}
AppPublisher=SizeShift
AppComments=Speicher analysieren, aufräumen und umlagern
DefaultDirName={localappdata}\Programs\SizeShift
DefaultGroupName=SizeShift
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=SizeShift-Setup-{#AppVersion}
SetupIconFile=..\src\TreeSizeBetter.App\SizeShift.ico
UninstallDisplayIcon={app}\SizeShift.exe
UninstallDisplayName=SizeShift
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

; Sprache des Assistenten richtet sich nach der Windows-Sprache (wie die App: de, en, fr, es).
[CustomMessages]
de.GroupShortcuts=Verknüpfungen:
en.GroupShortcuts=Shortcuts:
fr.GroupShortcuts=Raccourcis :
es.GroupShortcuts=Accesos directos:
de.GroupExplorer=Explorer:
en.GroupExplorer=Explorer:
fr.GroupExplorer=Explorateur :
es.GroupExplorer=Explorador:
de.TaskStartMenu=Eintrag im Startmenü
en.TaskStartMenu=Start menu entry
fr.TaskStartMenu=Entrée dans le menu Démarrer
es.TaskStartMenu=Entrada en el menú Inicio
de.TaskDesktop=Verknüpfung auf dem Desktop
en.TaskDesktop=Desktop shortcut
fr.TaskDesktop=Raccourci sur le Bureau
es.TaskDesktop=Acceso directo en el escritorio
de.TaskExplorer=„Mit SizeShift analysieren“ im Rechtsklick-Menü von Ordnern und Laufwerken
en.TaskExplorer="Analyze with SizeShift" in the right-click menu of folders and drives
fr.TaskExplorer=« Analyser avec SizeShift » dans le menu contextuel des dossiers et lecteurs
es.TaskExplorer=«Analizar con SizeShift» en el menú contextual de carpetas y unidades
de.ContextMenu=Mit SizeShift analysieren
en.ContextMenu=Analyze with SizeShift
fr.ContextMenu=Analyser avec SizeShift
es.ContextMenu=Analizar con SizeShift
de.ShortcutComment=Speicher analysieren, aufräumen und umlagern
en.ShortcutComment=Analyze, clean up and relocate disk space
fr.ShortcutComment=Analyser, nettoyer et déplacer l'espace disque
es.ShortcutComment=Analizar, limpiar y trasladar espacio en disco
de.LaunchNow=SizeShift jetzt starten
en.LaunchNow=Launch SizeShift now
fr.LaunchNow=Lancer SizeShift maintenant
es.LaunchNow=Iniciar SizeShift ahora

[Tasks]
Name: "startmenu";   Description: "{cm:TaskStartMenu}"; GroupDescription: "{cm:GroupShortcuts}"
Name: "desktopicon"; Description: "{cm:TaskDesktop}";   GroupDescription: "{cm:GroupShortcuts}"
Name: "explorer";    Description: "{cm:TaskExplorer}";  GroupDescription: "{cm:GroupExplorer}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\SizeShift";  Filename: "{app}\SizeShift.exe"; Comment: "{cm:ShortcutComment}"; Tasks: startmenu
Name: "{userdesktop}\SizeShift";   Filename: "{app}\SizeShift.exe"; Comment: "{cm:ShortcutComment}"; Tasks: desktopicon

[Registry]
; Rechtsklick auf einen Ordner
Root: HKCU; Subkey: "Software\Classes\Directory\shell\SizeShift"; ValueType: string; ValueName: ""; ValueData: "{cm:ContextMenu}"; Flags: uninsdeletekey; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Directory\shell\SizeShift"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\SizeShift.exe"",0"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Directory\shell\SizeShift\command"; ValueType: string; ValueName: ""; ValueData: """{app}\SizeShift.exe"" --scan ""%1"""; Tasks: explorer
; Rechtsklick in den leeren Bereich eines geoeffneten Ordners
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\SizeShift"; ValueType: string; ValueName: ""; ValueData: "{cm:ContextMenu}"; Flags: uninsdeletekey; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\SizeShift"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\SizeShift.exe"",0"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\SizeShift\command"; ValueType: string; ValueName: ""; ValueData: """{app}\SizeShift.exe"" --scan ""%V"""; Tasks: explorer
; Rechtsklick auf ein Laufwerk
Root: HKCU; Subkey: "Software\Classes\Drive\shell\SizeShift"; ValueType: string; ValueName: ""; ValueData: "{cm:ContextMenu}"; Flags: uninsdeletekey; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Drive\shell\SizeShift"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\SizeShift.exe"",0"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Drive\shell\SizeShift\command"; ValueType: string; ValueName: ""; ValueData: """{app}\SizeShift.exe"" --scan ""%1"""; Tasks: explorer

[Run]
Filename: "{app}\SizeShift.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Nur Programmdateien. Verlauf, Protokoll und Exporte in Dokumente\SizeShift
; bleiben bewusst liegen - dort steht, welche Ordner umgelagert sind.
Type: dirifempty; Name: "{app}"
