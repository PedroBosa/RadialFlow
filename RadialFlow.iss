; RadialFlow - instalador (Inno Setup 6)
; Gerado/chamado por publish.ps1, que passa /DAppVersion e /DSourceDir.
; Para compilar à mão: ISCC /DAppVersion=1.2.0 /DSourceDir=dist\RadialFlow-1.2.0-win-x64 RadialFlow.iss

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "dist\RadialFlow-" + AppVersion + "-win-x64"
#endif

[Setup]
; NÃO mude este GUID entre versões: é ele que faz a atualização substituir a instalação anterior.
AppId={{6F3B8C52-1D4E-4A97-9B2A-7C5E0D81F4A3}
AppName=RadialFlow
AppVersion={#AppVersion}
AppVerName=RadialFlow {#AppVersion}
AppPublisher=RadialFlow
VersionInfoVersion={#AppVersion}
; Program Files é obrigatório na prática: o app roda como administrador e a tarefa de
; inicialização usa privilégios altos, então o .exe NÃO pode ficar em pasta gravável por usuário comum.
DefaultDirName={autopf}\RadialFlow
DefaultGroupName=RadialFlow
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\RadialFlow.exe
SetupIconFile=Assets\RadialFlow.ico
OutputBaseFilename=RadialFlow-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\RadialFlow"; Filename: "{app}\RadialFlow.exe"
Name: "{group}\Desinstalar RadialFlow"; Filename: "{uninstallexe}"
Name: "{autodesktop}\RadialFlow"; Filename: "{app}\RadialFlow.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RadialFlow.exe"; Description: "Abrir o RadialFlow agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove a tarefa de "Iniciar com o Windows" criada pelo app (erro se não existir é ignorado).
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""RadialFlow"" /F"; Flags: runhidden; RunOnceId: "DelStartupTask"

[Code]
// Garante que o RadialFlow não esteja rodando antes de atualizar/remover.
procedure KillApp();
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM RadialFlow.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillApp();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then KillApp();
  // Configurações e logs em %LocalAppData%\RadialFlow ficam de propósito (o usuário pode querer mantê-los).
end;
