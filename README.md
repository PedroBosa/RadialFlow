# RadialFlow

Menu radial contextual para Windows. **Segure e arraste o botão direito** do mouse e uma roda de ações aparece
sob o cursor; solte sobre uma fatia para executá-la. O que a roda oferece depende do que está sob o cursor.

| Sob o cursor | Fatias |
|---|---|
| Arquivo ou pasta | Comprimir · Converter (submenu de formatos) · Copiar caminho · Mover para · Enviar para · Renomear |
| Barra de título | Maximizar · Snap esquerda/direita · Transparência · Sempre no topo · Minimizar · Fechar |
| Campo de texto | Colar · Traduzir · Pesquisar · Formatar · Limpar |
| Área de trabalho / fundo | Apps favoritos · Sistema · Área de transferência · Bloquear |

Um clique direito simples, sem arrastar, continua abrindo o menu normal do Windows. Fatias com a seta ⌄ têm submenu:
mantenha o cursor sobre elas (o tempo é configurável) e um traço na borda mostra quando ele abre.

## Requisitos

- Windows 10 ou 11 (a roda usa o tema claro/escuro e a cor de destaque do sistema).
- [.NET 10 SDK](https://dotnet.microsoft.com/download) para compilar (o `global.json` fixa a faixa do SDK).
- Para **converter** arquivos, instale as ferramentas que quiser usar em `C:\Program Files`: FFmpeg, ImageMagick,
  Pandoc e/ou LibreOffice. Imagens PNG/JPG/BMP/TIFF/GIF convertem sem instalar nada.

## Compilar, testar e executar

```powershell
dotnet build                 # aplicativo + testes + gerador de ícone; avisos são erros
dotnet test                  # suíte completa (xUnit)
dotnet build -c Release
.\bin\Release\net10.0-windows\RadialFlow.exe
```

O aplicativo **exige administrador** (`app.manifest`): é o que permite interceptar o mouse sobre janelas elevadas,
como o Gerenciador de Tarefas. Rode o `.exe` com "Executar como administrador". `dotnet run` num terminal sem
elevação falha com o erro 740, por causa do manifesto.

Ele vive na **bandeja do sistema** (anel azul-e-branco). Clique no ícone para abrir as Configurações; o menu de
contexto tem Pausar/Retomar, Configurações, Abrir pasta de logs e Sair. Abrir uma segunda cópia apenas pede à
primeira para mostrar as Configurações.

## Distribuir

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

O script roda os testes e, se passarem, gera em `dist\` (pasta fora do controle de versão):

- `RadialFlow-<versão>-win-x64\RadialFlow.exe`: executável único e autocontido (não exige o .NET instalado);
- `RadialFlow-<versão>-win-x64.zip`: a versão portátil;
- `RadialFlow-<versão>-Setup.exe`: instalador, só se o [Inno Setup 6](https://jrsoftware.org/isinfo.php) estiver
  instalado (`RadialFlow.iss`);
- `SHA256SUMS.txt`: hashes para conferir a integridade.

A versão vem do `<Version>` do `RadialFlow.csproj`. Prefira o instalador: ele coloca o app em `C:\Program Files`,
uma pasta que usuários comuns não podem alterar. Isso importa porque o app roda como administrador, e a tarefa
"Iniciar com o Windows" o abre com privilégios altos.

## Onde ficam os dados

| O quê | Onde |
|---|---|
| Configurações | `%LocalAppData%\RadialFlow\settings.json` (gravação atômica; cópia anterior em `settings.json.bak`) |
| Configuração corrompida | guardada como `settings.corrompido-AAAAMMDD-HHMMSS.json` ao lado (nunca é apagada) |
| Logs | `%LocalAppData%\RadialFlow\logs\radialflow.log` (rotação por tamanho; nível via `RADIALFLOW_LOG=debug`) |

## Segurança

O aplicativo roda como administrador e age sobre caminhos e processos de terceiros, então:

- **Ferramentas de conversão nunca rodam como administrador.** São iniciadas com o token não elevado do Explorer
  (`UnelevatedLauncher.ExecuteAsync`), dentro de um Job Object que encerra a árvore de processos ao cancelar. Se não
  houver token não elevado disponível (ex.: UAC desligado), a conversão externa é recusada.
- **Só executa programas em locais que usuários comuns não podem alterar** (`TrustedPath`: dono, ACL do arquivo, da
  pasta e dos ancestrais). Por isso ferramentas em pastas do usuário (`C:\ffmpeg`, WinGet…) são ignoradas de
  propósito: instale em `C:\Program Files`.
- Todo caminho vindo do Explorer, de configurações ou de diálogos é validado: sem `..`, streams alternativos
  (`arquivo:x`), nomes reservados (CON, NUL…) nem namespaces de dispositivo (`\\?\`).
- Extração de `.zip` à prova de *Zip Slip*. Nada é sobrescrito sem perguntar (`nome (1).ext`).

## Estrutura

```
RadialFlow.sln
├─ RadialFlow.csproj          o aplicativo (WPF, net10.0-windows)
├─ App.xaml(.cs)              partida: instância única, serviços, tratamento global de exceções
├─ Input/                     hook global do mouse (thread própria) e lista de programas ignorados
├─ Context/                   detecção do que está sob o cursor, fatias e ações
├─ Services/                  conversão, compactação, ferramentas externas, lançador sem elevação
├─ Security/                  TrustedPath: validação de caminhos e de executáveis
├─ Config/                    configurações e persistência atômica
├─ Infrastructure/            log em arquivo, instância única, versão
├─ UI/
│  ├─ Radial/                 a roda (janela, geometria, vidro fosco, posicionamento, tema)
│  ├─ Windows/                Configurações, Progresso, Conversão e diálogos
│  ├─ Tray/                   ícone da bandeja e renderizador do ícone do aplicativo
│  └─ Theme/                  estilos e paleta compartilhados por todas as janelas
├─ Assets/RadialFlow.ico      ícone do executável (gerado; ver abaixo)
├─ tools/IconGenerator/       gera o .ico a partir do mesmo código que desenha a bandeja
├─ publish.ps1, RadialFlow.iss  publicação (exe único, zip portátil) e instalador Inno Setup
└─ RadialFlow.Tests/          testes (espelham a estrutura do aplicativo)
```

### Ícone

O ícone é o próprio pie: um anel de fatias com uma fatia ativa em azul, desenhado com a mesma geometria da roda real
(`UI/Tray/AppIconRenderer.cs`). O arquivo `Assets/RadialFlow.ico` é **gerado**, não desenhado à mão:

```powershell
dotnet run --project tools/IconGenerator
```

Um teste (`ArquivoIcoVersionado_ConfereComOQueOCodigoDesenha`) falha se o `.ico` ficar desatualizado em relação ao código.

## Convenções

- C# moderno com *nullable* ligado; imports explícitos (`ImplicitUsings` desligado, para não colidir
  `System.IO.Path` com `System.Windows.Shapes.Path`).
- Análise de código no modo recomendado e avisos tratados como erro (`Directory.Build.props`); as exceções à política
  estão documentadas, com o motivo, no `.editorconfig`.
- Callbacks do Win32 rodam fora da thread de UI: devem apenas enfileirar trabalho no `Dispatcher`, nunca bloquear e
  nunca deixar uma exceção escapar. Todo handle nativo tem dono claro e é liberado de forma determinística.
- Mudanças relevantes entram no [CHANGELOG](CHANGELOG.md).

## Licença

Ainda não definida. Antes de publicar ou distribuir, escolha uma licença e adicione um arquivo `LICENSE`.
