; RadialFlow - instalador (Inno Setup 6)
; Chamado pelo publish.ps1, que passa /DAppVersion, /DSourceDir e, se houver certificado, /DSignTool.
; Para compilar à mão: ISCC /DAppVersion=1.3.0 /DSourceDir=dist\RadialFlow-1.3.0-win-x64 RadialFlow.iss

#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#ifndef SourceDir
  #define SourceDir "dist\RadialFlow-" + AppVersion + "-win-x64"
#endif

#define AppName      "RadialFlow"
#define AppPublisher "Pedro Lucas Ferreira Bosa"
#define AppCopyrightHolder "Pedro Bosa"
#define AppExe       "RadialFlow.exe"

[Setup]
; NÃO mude este GUID entre versões: é ele que faz uma instalação nova substituir a anterior.
AppId={{6F3B8C52-1D4E-4A97-9B2A-7C5E0D81F4A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=Menu radial para o Windows: segure o botão direito, arraste e escolha.
AppCopyright=Copyright © 2026 {#AppCopyrightHolder}

; Detalhes do próprio Setup.exe (aparecem nas propriedades do arquivo).
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Instalador do {#AppName}
VersionInfoCopyright=Copyright © 2026 {#AppCopyrightHolder}

; Program Files é obrigatório: o app roda como administrador, e a tarefa "Iniciar com o Windows"
; o abre com privilégios altos. Numa pasta que qualquer programa pode alterar, isso seria uma brecha.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=Assets\RadialFlow.ico
OutputBaseFilename={#AppName}-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Fecha o RadialFlow aberto antes de copiar os arquivos (pelo Gerenciador de Reinicialização do Windows).
CloseApplications=force
RestartApplications=no

; Assinatura digital (opcional): o publish.ps1 define SignTool só quando há um certificado.
#ifdef SignTool
SignTool=radialflowsign
SignedUninstaller=yes
#endif

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[CustomMessages]
brazilianportuguese.DesktopIcon=Criar um atalho na Área de Trabalho
brazilianportuguese.OpenNow=Abrir o RadialFlow agora
brazilianportuguese.ClosingApp=Fechando o RadialFlow…
brazilianportuguese.ConvertersGroup=Conversores (opcional). São programas gratuitos, baixados do site oficial de cada um e conferidos antes de instalar. Precisa de internet. Dá para instalar depois, em Configurações > Conversores.
brazilianportuguese.ConvFFmpeg=FFmpeg: vídeo e áudio (MP4, MP3, GIF animado...). Download de 106 MB
brazilianportuguese.ConvPandoc=Pandoc: Markdown e EPUB. Download de 40 MB
brazilianportuguese.ConvImageMagick=ImageMagick: WEBP, SVG e outros formatos de imagem. Download de 23 MB
brazilianportuguese.ConvLibreOffice=LibreOffice: Word, Excel e PowerPoint para PDF (instala a suíte completa). Download de 358 MB
brazilianportuguese.InstallingConverters=Baixando e instalando os conversores. Pode levar alguns minutos…

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
; Os já instalados nem aparecem (Check). Só o FFmpeg vem marcado: os outros são grandes ou mais específicos.
Name: "ffmpeg"; Description: "{cm:ConvFFmpeg}"; GroupDescription: "{cm:ConvertersGroup}"; Check: FFmpegMissing
Name: "pandoc"; Description: "{cm:ConvPandoc}"; GroupDescription: "{cm:ConvertersGroup}"; Flags: unchecked; Check: PandocMissing
Name: "imagemagick"; Description: "{cm:ConvImageMagick}"; GroupDescription: "{cm:ConvertersGroup}"; Flags: unchecked; Check: ImageMagickMissing
Name: "libreoffice"; Description: "{cm:ConvLibreOffice}"; GroupDescription: "{cm:ConvertersGroup}"; Flags: unchecked; Check: LibreOfficeMissing

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Direto em "Programas" (sem uma pasta com um item só); o desinstalar fica em Configurações > Aplicativos.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Menu radial para o Windows"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[InstallDelete]
; Versões de teste anteriores criavam o atalho numa pasta "RadialFlow" do menu Iniciar; ao atualizar, ela sai.
Type: filesandordirs; Name: "{autoprograms}\{#AppName}"

[Run]
; Conversores marcados: o próprio RadialFlow baixa, confere o SHA-256 e instala (janela de progresso). Roda como
; administrador (padrão de uma entrada sem "postinstall") e o instalador espera terminar.
Filename: "{app}\{#AppExe}"; Parameters: "--install-converters {code:SelectedConverters}"; StatusMsg: "{cm:InstallingConverters}"; Flags: waituntilterminated runascurrentuser; Check: HasSelectedConverters
; O app exige administrador (manifesto). Uma caixa "postinstall" roda, por padrão, com as credenciais SEM
; administrador de quem abriu o instalador, e aí o Windows recusa abrir o app (erro 740). "runascurrentuser"
; usa as credenciais do próprio instalador, que já é administrador: o app abre sem perguntar de novo.
Filename: "{app}\{#AppExe}"; Description: "{cm:OpenNow}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; Remove a tarefa "Iniciar com o Windows" criada pelo app (se não existir, o erro é ignorado).
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""RadialFlow"" /F"; Flags: runhidden; RunOnceId: "DelStartupTask"

[UninstallDelete]
; O FFmpeg baixado pela aba Conversores fica em {app}\tools. Pandoc, ImageMagick e LibreOffice são programas
; à parte (aparecem sozinhos em Aplicativos instalados) e continuam instalados.
Type: filesandordirs; Name: "{app}\tools"

[Code]
// ---- Conversores -------------------------------------------------------------------------------------------------
// Mesmos lugares em que o RadialFlow procura (ToolResolver): se já existe, a opção nem aparece.

function FFmpegMissing: Boolean;
begin
  Result := not (FileExists(ExpandConstant('{app}\tools\ffmpeg\ffmpeg.exe')) or
                 FileExists(ExpandConstant('{autopf}\ffmpeg\bin\ffmpeg.exe')) or
                 FileExists(ExpandConstant('{autopf}\WinGet\Links\ffmpeg.exe')));
end;

function PandocMissing: Boolean;
begin
  Result := not FileExists(ExpandConstant('{autopf}\Pandoc\pandoc.exe'));
end;

function LibreOfficeMissing: Boolean;
begin
  Result := not FileExists(ExpandConstant('{autopf}\LibreOffice\program\soffice.exe'));
end;

// O ImageMagick instala numa pasta com a versão no nome (ImageMagick-7.1.2-Q16-HDRI).
function ImageMagickMissing: Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if FindFirst(ExpandConstant('{autopf}\ImageMagick-*'), FindRec) then
  try
    repeat
      if FileExists(ExpandConstant('{autopf}\') + FindRec.Name + '\magick.exe') then Result := False;
    until (not Result) or (not FindNext(FindRec));
  finally
    FindClose(FindRec);
  end;
end;

procedure AddIfSelected(var List: String; const Id: String);
begin
  if WizardIsTaskSelected(Id) then
  begin
    if List <> '' then List := List + ',';
    List := List + Id;
  end;
end;

// "ffmpeg,pandoc": a lista que o RadialFlow recebe em --install-converters.
function SelectedConverters(Param: String): String;
begin
  Result := '';
  AddIfSelected(Result, 'ffmpeg');
  AddIfSelected(Result, 'pandoc');
  AddIfSelected(Result, 'imagemagick');
  AddIfSelected(Result, 'libreoffice');
end;

function HasSelectedConverters: Boolean;
begin
  Result := SelectedConverters('') <> '';
end;

// ---- Fechar o app ------------------------------------------------------------------------------------------------
// Encerra o RadialFlow antes de atualizar ou desinstalar. Ele não tem janela principal (vive perto do
// relógio), então o pedido educado do Windows nem sempre basta: aqui ele é encerrado de vez.
procedure CloseRadialFlow();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('ClosingApp');
  CloseRadialFlow();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then CloseRadialFlow();
  // As configurações e os logs em %LocalAppData%\RadialFlow ficam: o usuário pode querer reinstalar.
end;
