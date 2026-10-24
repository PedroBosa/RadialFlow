# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/). A versão vem de `RadialFlow.csproj`.

## [1.2.0] — 2026-10-24

### Adicionado
- **Ícone do aplicativo**: o próprio pie (anel de fatias com cantos arredondados, uma em azul, miolo no centro),
  desenhado com a mesma geometria da roda. Usado no executável, na barra de tarefas, nas janelas e na bandeja.
  O arquivo `Assets/RadialFlow.ico` (9 tamanhos, 16–256 px) é **gerado** por `tools/IconGenerator`, e um teste falha
  se ele ficar diferente do que o código desenha. Abaixo de 28 px o anel tem 4 fatias e uma fresta mais larga, para
  continuar legível.
- Ícone da **bandeja** no tamanho exato do sistema para o DPI (antes era um 32 px fixo, esticado), com o anel claro ou
  escuro conforme o tema da barra de tarefas, e em cinza quando pausado. Troca de tema renova o ícone.
- **Log em arquivo** (`%LocalAppData%\RadialFlow\logs\radialflow.log`): fila assíncrona, rotação por tamanho e nunca
  bloqueia o hook do mouse. Substitui 87 `Debug.WriteLine`, que não faziam nada em Release. Exceções globais, falhas
  de ação, de hook e de ferramentas agora deixam rastro.
- **Instância única**: abrir uma segunda cópia (ex.: atalho + "Iniciar com o Windows") não instala um segundo hook; a
  segunda pede à primeira para abrir as Configurações e encerra.
- **Ícone da bandeja volta** quando o Explorer reinicia (`TaskbarCreated`, liberado no UIPI porque o app é administrador).
- Menu da bandeja: **Abrir pasta de logs**. Aba *Sobre*: versão real, caminho do log, abrir e copiar.
- **Roda no estilo "pie"**: anel contínuo de vidro fosco (o fundo é capturado e desfocado), cantos arredondados, cor de
  destaque configurável (ou a do Windows), tema claro/escuro do sistema. Ícone e nome acompanham a fatia ativa e o miolo
  mostra o ícone da ação.
- **Tempo para abrir submenus configurável** (200–2000 ms; padrão 650 ms, antes fixo em 260 ms), com um traço na borda
  da fatia mostrando o tempo restante.
- A roda é **deslocada para caber na tela** perto de bordas e da barra de tarefas (o cursor acompanha).
- `UnelevatedLauncher.ExecuteAsync`: executa ferramentas de conversão **sem elevação** (token do shell), captura
  stdout/stderr em tempo real e cancela de verdade (Job Object encerra a árvore de processos).
- Projeto de testes `RadialFlow.Tests` (xUnit) com 700+ testes.
- **Distribuição**: `publish.ps1` gera um executável único e autocontido, um zip portátil, hashes SHA-256 e, se o
  Inno Setup 6 estiver instalado, um instalador (`RadialFlow.iss`) que instala em `C:\Program Files` e remove a
  tarefa de inicialização ao desinstalar.

### Alterado
- **Janelas redesenhadas** (Configurações, Progresso, Conversão, diálogos, seletor de favoritos) com um único sistema de
  estilo: cores do tema, chaves liga/desliga, sliders, bolinhas de cor e abas em pílula. As `MessageBox` brancas foram
  trocadas por diálogos no mesmo estilo (validação de nome dentro do próprio diálogo).
- **Configurações**: cada aba rola (antes o conteúdo era cortado), organizadas em Sistema / Gesto / Aparência.
- Fechar a janela de progresso no meio **cancela** a operação (antes ela seguia rodando invisível).
- Qualidade: análise de código recomendada ligada e avisos tratados como erro (`Directory.Build.props`).
- Estrutura de pastas por responsabilidade: `UI/Radial`, `UI/Windows`, `UI/Tray`, `UI/Theme`; os testes espelham o app.

### Corrigido
- **Configurações não se perdem mais**: gravação atômica (arquivo temporário + troca) com cópia `.bak`. Antes, um
  `settings.json` corrompido era sobrescrito pelos padrões, apagando blacklist e favoritos; agora o arquivo ruim é
  guardado como `settings.corrompido-AAAAMMDD-HHMMSS.json` e a cópia anterior é usada.
- Caminhos vindos do Explorer, de configurações e de diálogos são validados (`TrustedPath.TryGetSafeFullPath`): sem
  travessia (`..`), streams alternativos, nomes reservados (CON, NUL…) nem namespaces de dispositivo.
- Renomear não aceita mais nome com caminho (`..\..\x`) — o app roda como administrador.
- `schtasks` (Iniciar com o Windows) lê o XML de uma pasta só de administradores, não de `%TEMP%`.
- "Abrir pasta" da janela de progresso abre o Explorer **sem elevação** e com o caminho validado.

### Segurança
- Nenhuma ferramenta de conversão externa roda como administrador; o executável é validado em `TrustedPath` (dono +
  ACL de arquivo, pasta e ancestrais) imediatamente antes de ser iniciado. Se não houver token não elevado disponível
  (ex.: UAC desligado), a conversão externa é recusada em vez de rodar elevada.

## [1.1.0] — 2026-10-23 (correções dos itens 1 a 7 e migração para .NET 10)

### Adicionado
- `Security/TrustedPath.cs` — só executa ferramentas em locais que usuários comuns não podem alterar (dono + ACL).
- `Services/UnelevatedLauncher.cs` — abre navegador e apps favoritos sem herdar a elevação (token do shell).
- `Services/FileNaming.cs` — nome único `nome (1).ext`; nada é sobrescrito sem perguntar.
- `Context/TextTarget.cs` — foca o campo certo (UIA `SetFocus` na origem do gesto) antes de enviar teclas.
- `Context/WindowFocus.cs` — foco por `AttachThreadInput` (sem o truque do Alt) e checagem da janela em primeiro plano.

### Alterado
- `net8.0-windows` → `net10.0-windows` (o .NET 8 sai de suporte em 10/11/2026).
- Hook do mouse em **thread dedicada** com message loop próprio e watchdog que reinstala o hook a cada 10 s.
- "Iniciar com o Windows" via **Tarefa Agendada** (privilégios mais altos); remove a chave `Run` antiga.
- Conversão: opções conforme as ferramentas reais; FLAC/OGG/AAC com codec correto; roteamento Pandoc × LibreOffice;
  PNG transparente → JPEG com fundo branco; saída parcial apagada.
- Colar/Limpar/Formatar/Traduzir/Pesquisar focam o campo certo e abortam se a janela errada estiver em foco; "Limpar"
  pede confirmação em documentos; "Mover" restaura o destino em caso de falha e suporta outro volume.

### Corrigido
- Zip Slip na extração; zip parcial apagado ao cancelar ou falhar; pastas vazias entram no zip.
- `ToolResolver` sem `where.exe`; só ferramentas confiáveis; cache de "não encontrado" expira em 30 s.

### Comportamentos que mudaram
- Ferramentas (ffmpeg, magick, pandoc, soffice) em pastas do usuário (ex.: `C:\ffmpeg`, WinGet) são **ignoradas** de
  propósito. Instale em `C:\Program Files`.
- Conversão que cairia em arquivo existente gera `nome (1).ext`.
- "Mover" pergunta o que fazer no conflito: parar, manter os dois ou substituir.
- Converter a partir de PDF não é mais oferecido (nenhum motor lê PDF com fidelidade).
