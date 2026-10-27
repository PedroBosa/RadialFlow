# Novidades

Cada versão, da mais nova para a mais antiga. O número da versão está em `RadialFlow.csproj`.

## Não lançado

### Novo

- **Atalho global para abrir a roda.** Em **Configurações → Geral**, grave uma combinação como `Ctrl+Alt+Space`.
  A roda abre no cursor e se escolhe a opção com um **clique**: o miolo volta um nível, um clique fora do anel
  fecha e o botão direito, o **Esc** ou o próprio atalho fecham sem escolher. O atalho vale também nos programas da
  Blacklist e em tela cheia; a pausa da bandeja o desliga.
  - A combinação é validada no **momento de salvar**: texto inválido ou combinação já usada por outro programa é
    recusado com um aviso, e o atalho anterior continua valendo — nada é gravado para falhar só no próximo início.
  - Durante a gravação, a combinação atual é solta temporariamente, então dá para regravar o mesmo atalho. Conflitos
    aparecem no próprio cartão e o arquivo só é gravado quando o registro é aceito.
  - O atalho é só um gatilho local (`RegisterHotKey`), com uma janela receptora invisível; ele não instala nenhum
    hook de teclado nem lê teclas fora da combinação registrada.

## 1.3.0 — 27/10/2026

### Novo

- **Menus personalizados.** A nova aba **Configurações → Menus** permite criar menus que só aparecem em certos
  aplicativos, janelas ou contextos. Cada item pode executar uma ação do RadialFlow, abrir um programa, um site, uma
  pasta ou um arquivo, enviar um atalho de teclado ou colar um texto.
  - Os itens **somam** à roda automática. Um item com a mesma ação de uma fatia automática sem opções extras assume
    o lugar dela, mantendo as demais fatias.
  - Condições por processo (sem `.exe`), trecho do título da janela, contexto e prioridade. Vários menus que casam
    ao mesmo tempo somam seus itens.
  - O editor mostra uma **prévia da roda**, marcando o que substitui uma fatia automática e o que é extra, e avisa
    quando os itens passam do limite de 8 fatias da roda.
  - Itens do tipo **Submenu** abrem um segundo anel com 2 a 8 opções, como **Apps favoritos**. A roda e a prévia
    marcam essas fatias com uma setinha ▾; o diálogo **Editar opções…** monta cada opção com rótulo, ícone e ação.
- **Rotinas nos menus personalizados.** Um item do tipo **Rotina** executa até 8 passos em sequência — ações
  permitidas do RadialFlow com pausas de 50 a 2.000 ms, somando até 5 s. Ações que abrem outra janela, como
  **Pesquisar**, só podem ser o último passo. O diálogo **Editar passos…** monta a sequência e mostra um resumo;
  rotinas inválidas não aparecem na roda e a prévia avisa.
- **Importar e exportar menus personalizados.** Os botões **Importar…** e **Exportar…** na aba Menus trocam somente os
  menus personalizados, num arquivo `.json` com identificador de formato e versão. A importação acrescenta cópias ao
  rascunho — nunca substitui os menus existentes —, gera identificadores novos, desempata nomes repetidos com o
  sufixo “(importado)” e recusa o arquivo inteiro se qualquer menu ou item estiver fora das regras. Nada é executado
  até você clicar em **Salvar**. A exportação grava o rascunho atual, inclusive alterações ainda não salvas, e usa
  gravação atômica para não deixar o arquivo escolhido pela metade.
- **Estilos visuais da roda.** **Padrão**, **Compacto**, **Amplo** e **Alto contraste** mudam o tamanho e a
  legibilidade da roda e do segundo anel. A cor de destaque continua valendo em todos; o **Alto contraste** usa
  superfícies opacas e contornos fortes, sem o fundo desfocado, e segue o tema claro/escuro do Windows. O tamanho
  acompanha a escala de DPI de cada monitor, inclusive na posição perto das bordas.
- **Conjunto curado de ícones nos menus.** O seletor de ícone agora oferece mais opções locais do Windows (arquivos,
  mídia, ações, avisos e sistema). Nada é baixado ou instalado: o conjunto é fixo e aparece igual no editor de itens e
  no de opções do submenu.
- **Interface em português, inglês e espanhol.** A aba **Configurações → Aparência** agora também escolhe o idioma do
  aplicativo, com português (Brasil) como padrão — sem depender do idioma do Windows. A escolha vale no próximo
  início e não reinstala nada. Roda, menus, bandeja, diálogos, conversores e mensagens de erro acompanham o idioma;
  o instalador continua em português.

### Melhorias

- **Configurações reorganizadas.** A janela foi reorganizada em cartões por aba, e a nova aba **Aparência** reúne
  cor de destaque, estilo da roda e idioma. Na aba **Menus**, **Duplicar** e **Remover** aparecem só com um menu
  selecionado.
- **Mais de 8 opções agora ficam acessíveis.** Quando uma lista não cabe na roda — por exemplo, uma imagem que aceita
  os 9 formatos de conversão —, a última fatia vira **Mais…** e abre a página seguinte, em vez de descartar as opções
  extras. A ordem original é preservada, e as páginas funcionam com mouse (segurar) e teclado (Enter ou o número).
- **A roda agora aceita teclado.** Com ela aberta, **1–8** escolhem a opção pela posição na roda (1 no topo,
  seguindo no sentido horário), as **setas** circulam, **Enter** abre o submenu ou executa e **Esc** volta ao menu
  anterior ou fecha. A escolha usa o mesmo destaque do mouse, que retoma o controle assim que se move; os atalhos
  existem apenas enquanto a roda está visível.
- **Salvar configurações agora confirma a gravação no disco antes de fechar.** Se o disco recusar, a janela continua
  aberta com o motivo e nada é aplicado — antes, ela fechava e as alterações podiam se perder no próximo início.
- **Fechar as Configurações com alterações não salvas avisa em qualquer aba**, listando as seções alteradas. Antes,
  só alterações em **Menus** eram lembradas.

### Segurança

- Itens externos dos menus seguem as mesmas regras do resto do programa: executáveis só de pastas protegidas, **sem
  argumentos** e com interpretadores (cmd, PowerShell, wscript etc.) bloqueados; sites só em `http://` ou
  `https://`; executáveis, scripts e atalhos são recusados em **Abrir arquivo ou pasta**.
- Programas abertos pelos menus rodam **sem** permissão de administrador. Apps instalados na pasta do usuário (como
  `%LocalAppData%`) são recusados em **Abrir executável**; para esses, continue usando **Apps favoritos**.
- Itens importados passam pelas mesmas validações da criação manual — tipos conhecidos, limites de tamanho, submenu e
  rotina nos lugares certos — e os alvos continuam sendo revalidados no momento da execução.
- Os presets visuais são só paleta e geometria locais: não carregam CSS, fontes, imagens nem recursos remotos.

## 1.2.0 — 24/10/2026

### Novo

- **Visual novo da roda.** Um anel de fatias com efeito de vidro fosco, que segue o tema claro ou escuro do Windows.
  A fatia sob o cursor fica na cor de destaque (você escolhe a cor nas configurações) e o centro mostra o ícone da
  ação.
- **Tempo para abrir as opções extras é configurável**, de 0,2 a 2 segundos (o padrão subiu de 0,26 para 0,65 s). Um
  traço na borda da fatia mostra quando as opções vão aparecer. Dentro das opções extras, segurar no centro desenha um
  círculo em volta do botão de voltar, no mesmo estilo.
- **Converter ficou bem mais completo**, e só mostra o que funciona no seu computador:
  - **Imagens** sem instalar nada: PNG, JPG, PDF, ICO (com todos os tamanhos de ícone), GIF, BMP e TIFF. Fotos WebP,
    HEIC, AVIF e RAW de câmera também, se as extensões de imagem gratuitas da Microsoft Store estiverem instaladas, e
    HEIC como destino quando o Windows consegue gravá-lo. Fotos de celular saem na orientação certa.
  - **Vídeo**: MP4, MKV, WEBM, MOV, GIF animado e extração do áudio (MP3, M4A, WAV).
  - **Áudio**: MP3, M4A, WAV, FLAC, OGG, OPUS, AAC e AIFF.
  - **Documentos** (Word, ODT, RTF, TXT, HTML, Markdown, EPUB), **planilhas** (Excel, ODS, CSV) e **apresentações**
    (PowerPoint, ODP), inclusive para PDF.
- Quando não há nenhuma conversão possível para o item (uma pasta, um formato desconhecido ou um programa que falta),
  a opção **Converter** nem aparece na roda.
- **Os conversores são instalados pelo próprio RadialFlow**: no instalador (o FFmpeg já vem marcado) ou depois, na
  nova aba **Configurações → Conversores**. Os arquivos vêm do site oficial de cada programa e só são instalados se a
  impressão digital (SHA-256) conferir. Antes era preciso instalar cada um à mão.
- **A roda sempre cabe na tela.** Perto das bordas ou da barra de tarefas, ela se desloca para dentro e o cursor vai
  junto.
- **Ícone novo**, no formato da própria roda, no programa, nas janelas e perto do relógio. Ali ele fica claro ou escuro
  conforme a barra de tarefas, e cinza quando o RadialFlow está pausado.
- **Instalador** em português, que coloca o programa em `C:\Program Files`. Há também uma versão portátil em `.zip`.
- **Registro de eventos** (`%LocalAppData%\RadialFlow\logs`), aberto pelo menu do ícone ou pela aba Sobre, para
  entender o que deu errado quando algo falha.
- Abrir o RadialFlow de novo não cria uma segunda cópia: só mostra as configurações da que já está aberta.
- O ícone volta sozinho perto do relógio se o Windows Explorer reiniciar.

### Melhorias

- Configurações, janela de progresso, janela de conversão e avisos ganharam o mesmo visual da roda. As caixas de
  mensagem brancas do Windows foram trocadas por avisos no mesmo estilo.
- As configurações agora rolam: antes, parte das opções ficava cortada.
- Fechar a janela de progresso no meio cancela a operação. Antes ela continuava escondida, sem como parar.
- A opção **Sistema** da área de trabalho virou **Ajustes rápidos** e abre as Configurações rápidas do Windows.

### Correções

- **As configurações não se perdem mais.** Antes, se o arquivo de configurações estragasse (por exemplo, numa queda
  de energia), o programa voltava aos padrões e apagava sua blacklist e seus favoritos. Agora ele usa a cópia
  anterior e guarda o arquivo estragado ao lado, sem apagar nada.
- Renomear não aceita mais um nome que leve o arquivo para outra pasta.
- A dica do Explorer ("Tipo de item, Tamanho...") não aparece mais por cima da roda aberta.
- **Abrir o RadialFlow agora**, no fim da instalação, dava o erro 740. Agora abre normalmente.
- Markdown para PDF não falha mais: antes o Pandoc exigia um programa extra (LaTeX) que quase ninguém tem; agora o
  PDF é feito pelo LibreOffice.
- CSV separado por ponto e vírgula (o padrão do Excel em português) abre com as colunas certas.
- Um programa de conversão que trava com um arquivo estranho é encerrado depois de 5 minutos, com uma mensagem clara,
  em vez de deixar a conversão esperando para sempre.

### Segurança

- Os programas de conversão (FFmpeg, ImageMagick, Pandoc, LibreOffice) agora rodam **sem** permissão de
  administrador, e cancelar uma conversão encerra o programa e tudo o que ele abriu.
- Antes de executar qualquer um deles, o RadialFlow confere se ele está numa pasta protegida, como
  `C:\Program Files`. Se o programa for um atalho do tipo link (como os que o winget cria), vale a pasta do arquivo
  real, não a do atalho.
- Caminhos de arquivo vindos do Explorer, das configurações e das janelas são conferidos antes de serem usados.
- Abrir uma pasta pela janela de progresso não dá mais permissão de administrador ao Explorer.

## 1.1.0 — 23/10/2026

### Melhorias

- O programa passou a usar o .NET 10 (o .NET 8 deixa de receber atualizações em novembro de 2026).
- **Iniciar com o Windows** agora funciona mesmo exigindo administrador: virou uma tarefa agendada, sem pedir
  permissão a cada entrada no Windows.
- O controle do mouse roda separado do resto do programa, então o mouse não trava se outra parte do RadialFlow
  demorar. Se o Windows desligar esse controle, o RadialFlow o religa sozinho.
- Colar, Limpar, Negrito, Traduzir e Pesquisar agem no campo certo e não fazem nada se outra janela estiver na
  frente. **Limpar** pede confirmação em documentos.
- **Mover** pergunta o que fazer quando já existe um arquivo com o mesmo nome: parar, manter os dois ou substituir.
  Também funciona entre discos diferentes.
- Conversão: só aparecem os formatos que o seu computador consegue gerar, e áudio em FLAC, OGG e AAC sai certo.
  Imagens PNG com fundo transparente viram JPG com fundo branco. Converter a partir de PDF deixou de ser oferecido,
  porque nenhum dos conversores lê PDF bem.

### Correções

- Extrair um `.zip` malicioso não consegue mais gravar fora da pasta escolhida.
- Cancelar ou falhar uma compactação ou conversão apaga o arquivo pela metade. Pastas vazias entram no `.zip`.
- Nada é sobrescrito sem perguntar: se o nome já existe, o novo arquivo vira `nome (1).ext`.

### Segurança

- Navegador, apps favoritos e pastas abertos pela roda não recebem permissão de administrador.
- Programas de conversão instalados em pastas do usuário (como `C:\ffmpeg`) são ignorados de propósito. Instale-os
  em `C:\Program Files`.
