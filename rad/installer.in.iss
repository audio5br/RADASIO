; Template do instalador %%BRAND%%.
; apply_branding.py substitui os marcadores %%...%% e grava o resultado em
; src/installer.in.iss; depois o installer.cmake (CMake) substitui @...@.
; Requer Inno Setup 6 ou superior.

[Setup]
AppId={{%%APPID%%}
AppName=%%BRAND%%
AppVerName=%%BRAND%% @FLEXASIO_VERSION@
AppVersion=@FLEXASIO_VERSION@
AppPublisher=%%PUBLISHER%%
AppPublisherURL=%%URL%%
AppSupportURL=%%SUPPORT_URL%%
WizardStyle=modern
SetupIconFile=..\..\rad\assets\rad.ico
WizardImageFile=..\..\rad\assets\wizard_large.bmp
WizardSmallImageFile=..\..\rad\assets\wizard_small.bmp
UninstallDisplayIcon={app}\%%FILE_BASE%%Settings.exe
LicenseFile=..\..\rad\NOTICES.txt

DefaultDirName={autopf}\%%BRAND%%
AppendDefaultDirName=no
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Driver (registra a DLL em cada arquitetura)
Source:"install\x64-Release\bin\%%FILE_BASE%%.dll"; DestDir: "{app}\x64"; Flags: ignoreversion regserver 64bit; Check: Is64BitInstallMode
Source:"install\x64-Release\bin\*"; DestDir: "{app}\x64"; Flags: ignoreversion 64bit; Check: Is64BitInstallMode
Source:"install\x86-Release\bin\%%FILE_BASE%%.dll"; DestDir: "{app}\x86"; Flags: ignoreversion regserver
Source:"install\x86-Release\bin\*"; DestDir: "{app}\x86"; Flags: ignoreversion
; App de configuracoes e perfis
Source:"..\..\rad\app\publish\%%FILE_BASE%%Settings.exe"; DestDir: "{app}"; Flags: ignoreversion
Source:"..\..\rad\profiles\*.toml"; DestDir: "{app}\profiles"; Flags: ignoreversion
Source:"..\..\rad\interfaces.json"; DestDir: "{app}"; Flags: ignoreversion
; Licencas e avisos
Source:"..\..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source:"..\..\rad\NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source:"..\portaudio\LICENSE.txt"; DestDir: "{app}\licenses"; DestName: "PortAudio-LICENSE.txt"; Flags: ignoreversion skipifsourcedoesntexist

[Registry]
; O botao "Control Panel" do driver le este valor para abrir o app de configuracoes.
Root: HKLM; Subkey: "Software\%%REG_VENDOR%%\Settings\Install"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey

[Icons]
Name: "{autoprograms}\%%BRAND%%\Configuracoes do %%BRAND%%"; Filename: "{app}\%%FILE_BASE%%Settings.exe"
Name: "{autoprograms}\%%BRAND%%\Desinstalar %%BRAND%%"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\%%FILE_BASE%%Settings.exe"; Description: "Abrir as configuracoes do %%BRAND%%"; Flags: postinstall nowait skipifsilent runasoriginaluser
