# Novidades

Cada versão, da mais nova para a mais antiga. O número da versão está em `RadialFlow.csproj`.

## 1.4.1 — 28/10/2026

### Novo

- **Menus por extensão e por classe de janela.** Além de processo, título e contexto, um menu pode valer só para
  certos tipos de arquivo (até 8 extensões) ou para uma classe técnica de janela (como `CabinetWClass`, do Explorer).
- **Transformar o texto selecionado.** Um item pode deixar a seleção em maiúsculas, minúsculas, título ou frase, sem
  espaços sobrando ou em uma linha: o RadialFlow copia, transforma e cola por cima, sem enviar nada para fora.
- **Atalho próprio por menu.** Cada menu pode ter um atalho global (como `Ctrl+Alt+1`) que abre a roda direto com os
  itens dele, no cursor e sem exigir as condições; repetir o atalho global ou o de outro menu é recusado.

### Melhorias

- **Configurações e diálogos mais diretos.** Paleta quase monocromática nos dois temas, acento restrito a foco,
  seleção e ação principal, cartões sem borda e controles finos, no mesmo tratamento das janelas de Conversão,
  Progresso, Submenu e Rotina; a ação principal vem antes de **Cancelar**, que começa com o foco nos avisos destrutivos.
- **Aba Menus reorganizada do zero.** Lista de menus à esquerda, com contagem e as ações de criar, importar e exportar;
  o menu selecionado ganha um cabeçalho fixo com nome editável, chave **Ativo**, duplicar/remover e um resumo ao vivo
  das condições; abaixo, três seções — **Itens**, **Aplicação** e **Prévia** — mostram um assunto por vez.
- **Espaçamentos e tamanhos padronizados** na aba Menus, com a largura dos controles igual à das outras abas.

### Correções

- **Temporários de conversão:** pastas de conversões interrompidas em `%TEMP%\RadialFlow` são removidas na
  inicialização seguinte, em vez de acumular espaço; o cabeçalho fixo da aba Menus se alinha aos cartões ao rolar.

### Para desenvolvedores

- **Repositório pronto para contribuir.** Código em `src/`, testes em `tests/`, solução única com pastas, `.editorconfig`,
  CI no GitHub Actions que compila e roda a suíte a cada push, e `publish.ps1` conferindo a versão do `.iss` contra o
  `.csproj` antes de gerar os arquivos de distribuição.

## 1.4.0 — 27/10/2026

### Novo

- **Atalho global para abrir a roda.** Em **Configurações → Geral**, grave uma combinação como `Ctrl+Alt+Space`: a
  roda abre no cursor e se escolhe com um **clique** — o miolo volta um nível, um clique fora fecha e o botão direito,
  o **Esc** ou o atalho fecham sem escolher. Vale na Blacklist e em tela cheia, e a pausa da bandeja o desliga. A
  combinação é validada ao salvar (texto inválido ou conflito é recusado, e o atalho anterior continua valendo) e usa
  só o `RegisterHotKey`, sem hook de teclado.

### Melhorias

- **Janelas no mesmo visual da roda**, com os mesmos cartões e destaques e textos legíveis nos dois temas; diálogos,
  conversão e progresso mostram um fundo desfocado do que está atrás, opaco em **Alto contraste**, fora da tela ou se
  a captura falhar, e os controles ganham transições de 110 ms, respeitando a opção de animações do Windows.

## 1.3.0 — 27/10/2026

### Novo

- **Menus personalizados** em **Configurações → Menus**: menus que só aparecem em certos aplicativos, janelas ou
  contextos, com itens que executam ações do RadialFlow ou abrem programas, sites, pastas, arquivos, atalhos ou texto.
  - Os itens somam à roda automática (um item com a mesma ação assume o lugar da fatia), e as condições são processo,
    título, contexto e prioridade; a **prévia** marca substituições e extras e avisa acima de 8 fatias.
  - **Submenu** abre um segundo anel com 2 a 8 opções em **Editar opções…** (rótulo, ícone e ação), como os **Apps
    favoritos**, com ▾ na fatia; o seletor de ícone é um conjunto fixo de opções do Windows, sem baixar nada.
  - **Rotina** executa até 8 passos — ações do RadialFlow com pausas de 50 a 2.000 ms, até 5 s —, e ações que abrem
    outra janela, como **Pesquisar**, só podem ser o último passo.
  - **Importar…** e **Exportar…** trocam só os menus num `.json` versionado: importar acrescenta cópias (repetidos
    ganham “(importado)”) e recusa o arquivo inteiro se algo violar as regras; exportar grava o rascunho atual.
- **Estilos visuais da roda.** **Padrão**, **Compacto**, **Amplo** e **Alto contraste** mudam o tamanho e a
  legibilidade da roda; o **Alto contraste** dispensa o desfoque e segue o tema do Windows, com DPI por monitor.
- **Interface em português, inglês e espanhol**, escolhido em **Configurações → Aparência** (português do Brasil por
  padrão), vale no próximo início; roda, menus, bandeja, diálogos, conversores e erros acompanham.

### Melhorias

- **Configurações reorganizadas** em cartões por aba, com a nova aba **Aparência** reunindo cor de destaque, estilo da
  roda e idioma; **Duplicar** e **Remover** só aparecem com um menu selecionado.
- **Mais de 8 opções ficam acessíveis** e a roda aceita teclado: o que não cabe vira **Mais…**, que abre a página
  seguinte na mesma ordem, e **1–8**, **setas**, **Enter** e **Esc** navegam enquanto ela está visível.
- **Salvar confirma a gravação no disco antes de fechar.** Se o disco recusar, a janela continua aberta com o motivo
  e nada é aplicado; fechar com alterações não salvas avisa em qualquer aba, listando as seções alteradas.

### Segurança

- Itens externos dos menus seguem as regras do programa: executáveis só de pastas protegidas, sem argumentos e com
  interpretadores (cmd, PowerShell, wscript) bloqueados; sites só `http`/`https`; scripts e atalhos recusados em
  **Abrir arquivo ou pasta**; nada roda como administrador, e `%LocalAppData%` fica fora de **Abrir executável**.
  Itens importados passam pelas mesmas validações, e os presets visuais são só paleta e geometria locais.

## 1.2.0 — 24/10/2026

### Novo

- **Visual novo da roda:** anel de fatias com vidro fosco que segue o tema do Windows, fatia na cor de destaque e
  ícone no centro; as opções extras abrem no tempo configurado (0,2 a 2 s, padrão 0,65 s), com um traço na borda.
- **Converter ficou bem mais completo** e só mostra o que funciona no seu computador:
  - **Imagens** sem instalar nada: PNG, JPG, PDF, ICO, GIF, BMP e TIFF — e WebP, HEIC, AVIF e RAW com as extensões
    gratuitas da Microsoft Store, HEIC como destino quando o Windows grava, e fotos de celular na orientação certa.
  - **Vídeo** para MP4, MKV, WEBM, MOV e GIF animado, com extração do áudio (MP3, M4A, WAV); **áudio** para MP3,
    M4A, WAV, FLAC, OGG, OPUS, AAC e AIFF.
  - **Documentos** (Word, ODT, RTF, TXT, HTML, Markdown, EPUB), **planilhas** (Excel, ODS, CSV) e **apresentações**
    (PowerPoint, ODP), inclusive para PDF.
- **Os conversores são instalados pelo próprio RadialFlow**, no instalador (o FFmpeg já vem marcado) ou na aba
  **Configurações → Conversores**, e só instalam se o SHA-256 do arquivo oficial conferir.
- **A roda sempre cabe na tela:** perto das bordas ou da barra de tarefas, ela se desloca para dentro e o cursor vai
  junto. **Ícone novo**, no formato da roda, claro ou escuro conforme a barra e cinza quando o app está pausado.
- **Instalador em português** em `C:\Program Files`, com versão portátil em `.zip` e **registro de eventos** em
  `%LocalAppData%\RadialFlow\logs`; abrir de novo só mostra as configurações da cópia aberta, e o ícone volta se o
  Explorer reiniciar.

### Melhorias

- Configurações, progresso, conversão e avisos ganharam o visual da roda, substituindo as caixas de mensagem brancas
  do Windows, e as configurações agora rolam.
- Fechar a janela de progresso no meio cancela a operação, em vez de continuar escondida sem como parar.
- A opção **Sistema** da área de trabalho virou **Ajustes rápidos** e abre as Configurações rápidas do Windows.

### Correções

- **As configurações não se perdem mais:** se o arquivo estragar (por exemplo, numa queda de energia), o programa usa
  a cópia anterior e guarda o arquivo estragado ao lado, em vez de voltar aos padrões e apagar blacklist e favoritos.
- Renomear não leva mais o arquivo para outra pasta, e a dica do Explorer (“Tipo de item, Tamanho...”) não aparece
  mais por cima da roda.
- **Abrir o RadialFlow agora**, no fim da instalação, funciona (antes dava o erro 740); Markdown para PDF não exige
  mais LaTeX, CSV com ponto e vírgula abre certo, e conversor travado é encerrado após 5 minutos.

### Segurança

- Os conversores rodam **sem** administrador, e cancelar encerra o programa e o que ele abriu; só executam de pastas
  protegidas como `C:\Program Files`, e abrir pasta pela janela de progresso não dá administrador ao Explorer.

## 1.1.0 — 23/10/2026

### Melhorias

- O programa passou a usar o .NET 10 (o .NET 8 deixa de receber atualizações em novembro de 2026).
- **Iniciar com o Windows** virou uma tarefa agendada, sem pedir permissão a cada entrada, e o controle do mouse roda
  separado, então o mouse não trava se outra parte demorar e se religa sozinho se o Windows o desligar.
- Colar, Limpar, Negrito, Traduzir e Pesquisar agem no campo certo e não fazem nada se outra janela estiver na frente
  (**Limpar** confirma em documentos); **Mover** pergunta o que fazer com nome repetido e funciona entre discos.
- Conversão: só aparecem os formatos que o computador consegue gerar, FLAC, OGG e AAC saem certos, PNG transparente
  vira JPG com fundo branco e PDF deixou de ser oferecido como origem.

### Correções

- Extrair um `.zip` malicioso não grava mais fora da pasta escolhida, cancelar ou falhar apaga o arquivo pela metade,
  e nada é sobrescrito sem perguntar: se o nome existe, o novo vira `nome (1).ext`; pastas vazias entram no `.zip`.

### Segurança

- Navegador, apps favoritos e pastas abertos pela roda não recebem administrador, e conversores em pastas do usuário
  (como `C:\ffmpeg`) são ignorados — instale-os em `C:\Program Files`.
