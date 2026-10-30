; Installateur Windows de Memo — Inno Setup 6
;
; Compilation (depuis la racine du dépôt) :
;   ISCC.exe installer\Memo.iss
; ou :
;   pwsh -File scripts\build-installer.ps1
;
; La publication attendue est décrite dans docs/release.md :
;   dotnet publish src\Memo.App\Memo.App.csproj -c Release -r win-x64 ^
;     --self-contained -p:PublishReadyToRun=false -o artifacts\publish
;
; Cet installateur n'est pas signé. La page d'avertissement le dit explicitement.

#define MyAppName "Memo"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Memo"
#define MyAppExeName "Memo.exe"
#define MyAppUrl "https://github.com/tomyrms/Memo"
#define PublishDir "..\artifacts\publish"
#define LicensesDir "..\LICENSES"
#define OutputDir "..\artifacts"

[Setup]
; Identifiant stable de l'application : ne pas le changer entre deux versions,
; sinon Windows considérera qu'il s'agit d'un autre produit.
AppId={{333400B3-4481-4627-A4C8-9D929C662481}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={localappdata}\Programs\Memo
DefaultGroupName=Memo
DisableProgramGroupPage=yes
; Installation par utilisateur, sans élévation ni dossier Program Files.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004 (build 19041) ou plus récent, x64 uniquement.
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Memo-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\src\Memo.App\Assets\Memo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
AllowNoIcons=yes
CloseApplications=yes
RestartApplications=no
; Page affichée avant l'installation : absence de signature et SmartScreen.
InfoBeforeFile=UNSIGNED-WARNING.txt
; Page affichée après l'installation : les données utilisateur restent en place.
InfoAfterFile=DATA-PRESERVATION.txt
VersionInfoVersion=0.1.0.0
VersionInfoCompany=Memo
VersionInfoDescription=Installateur Memo
VersionInfoProductName=Memo
VersionInfoProductVersion=0.1.0
; Aucun SignTool n'est configuré : la sortie reste non signée.

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Les symboles de débogage et les binaires LibVLC des autres architectures ne
; sont pas nécessaires à l'exécution x64. Les DLL LibVLC restent des fichiers
; séparés, remplaçables dans {app}\libvlc\win-x64 (conformité LGPL).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb,*.lib,libvlc\win-arm64\*,libvlc\win-x86\*"
Source: "{#LicensesDir}\*"; DestDir: "{app}\LICENSES"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#LicensesDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Memo"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\Memo"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,Memo}"; Flags: nowait postinstall skipifsilent

; Aucune entrée [UninstallDelete] : la désinstallation retire uniquement les
; fichiers installés. Les bibliothèques et données de l'utilisateur, créées
; hors du dossier d'installation, ne sont jamais supprimées.
