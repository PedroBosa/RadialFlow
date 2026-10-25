# Novidades

Cada versão, da mais nova para a mais antiga. O número da versão está em `RadialFlow.csproj`.

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
