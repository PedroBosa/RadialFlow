# RadialFlow

> **Menu radial contextual de alta performance para o ecossistema Windows. Desenvolvido em .NET e C# com aceleração gráfica em WPF, interceptação de eventos via ganchos Win32 de baixo nível em thread dedicada e contenção de privilégios de interface (UIPI).**

<p align="center">
  <a href="https://github.com/PedroBosa/RadialFlow/releases/latest"><img alt="Download da Versão" src="https://img.shields.io/badge/Download-Release-10B981?style=for-the-badge&logo=windows"></a>
  <a href="LICENSE"><img alt="Licença: MIT" src="https://img.shields.io/badge/Licen%C3%A7a-MIT-6366F1?style=for-the-badge"></a>
  <a href="https://github.com/PedroBosa"><img alt="Autor: Pedro Bosa" src="https://img.shields.io/badge/Autor-Pedro%20Bosa-4F46E5?style=for-the-badge&logo=github"></a>
</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet">
  <img alt="C# 12" src="https://img.shields.io/badge/C%23-12-239120?style=flat&logo=csharp">
  <img alt="WPF / Win32" src="https://img.shields.io/badge/GUI-WPF%20%2F%20Win32-0078D4?style=flat&logo=windows">
  <img alt="Windows 10 e 11" src="https://img.shields.io/badge/Plataforma-Windows%2010%20%2F%2011%20(x64)-2563EB?style=flat&logo=windows">
  <img alt="Status do CI" src="https://github.com/PedroBosa/RadialFlow/actions/workflows/ci.yml/badge.svg">
</p>

---

## Visão geral

O **RadialFlow** é um sistema de produtividade e acesso contextual rápido baseado em geometria radial (*pie menu*) para estações de trabalho Windows.

Os menus de contexto tradicionais do sistema operacional baseiam-se em listas verticais lineares cuja usabilidade é restrita pela **Lei de Fitts**: o tempo necessário para mover o cursor até um item alvo é proporcional à distância linear percorrida e inversamente proporcional à largura do elemento ($T = a + b \log_2(2D/W + 1)$). Conforme mais aplicativos injetam extensões no registro do Windows Explorer, o menu torna-se mais extenso e lento, exigindo varreduras visuais contínuas e percursos motores imprecisos.

O RadialFlow elimina esse gargalo distribuindo ações em fatias circulares equidistantes do ponto inicial do cursor ($D = \text{constante}$). Ao segurar o botão direito e realizar um microarrasto angular, a roda surge instantaneamente sob o cursor sem roubar o foco da janela ativa (`WS_EX_NOACTIVATE`), viabilizando acionamentos musculares em fração de segundo.

---

## Destaques de engenharia

### 1. Cinemática de gesto e ganchos Win32 em thread dedicada
A interceptação global do mouse utiliza o gancho de baixo nível `WH_MOUSE_LL` da API Win32. 

Em implementações ingênuas, o gancho executa na thread de despacho da interface gráfica (WPF). Qualquer operação bloqueante na interface (diálogos, consultas I/O de arquivos ou acessos à área de transferência) atrasa a resposta do gancho em nível de sistema; se o atraso ultrapassar o limite `LowLevelHooksTimeout` definido no registro do Windows, o sistema operacional desconecta o gancho silenciosamente.

O RadialFlow resolve este desafio arquitetural com:
- **Thread STA isolada com message loop Win32 nativo:** o gancho opera em uma thread dedicada executando um loop puro `GetMessage` / `TranslateMessage` / `DispatchMessage`. Nenhuma chamada síncrona de I/O ocorre nessa thread; os eventos capturados (`DragStarted`, `DragMoved`, `DragCompleted`) são despachados de forma assíncrona para o `Dispatcher` do WPF.
- **Zona morta quadrática (*deadzone*):** o cálculo de transição entre o clique estático do botão direito e o início do arrasto radial utiliza comparação quadrática:
  
  $$d^2 = (x - x_0)^2 + (y - y_0)^2 > r_{\text{deadzone}}^2$$
  
  com $r_{\text{deadzone}} \ge 15\text{ px}$. A formulação quadrática descarta a necessidade de invocar funções de raiz quadrada (`Math.Sqrt`) a cada mensagem `WM_MOUSEMOVE` de alta frequência reportada pelo mouse.
- **Assinatura e reinjeção de cliques via `SendInput`:** quando o usuário realiza um clique direito simples (sem ultrapassar a zona morta), o RadialFlow reinjeta a sequência original `MOUSEEVENTF_RIGHTDOWN` e `MOUSEEVENTF_RIGHTUP` contendo a assinatura `0x52464C57` ("RFLW") no campo `dwExtraInfo`, instruindo o gancho a ignorar o evento reinjetado e garantindo a abertura pontual do menu nativo do Windows.
- **Watchdog autorrecuperável:** um temporizador de watchdog verifica o estado do gancho a cada $10\text{ s}$ de repouso (`Idle`). Caso o subsistema do Windows tenha removido o gancho em segundo plano, a thread do serviço o reinstala preventivamente.

### 2. Modelo de contenção de privilégios UIPI e *Unelevated Launcher*
O executável do RadialFlow requer privilégios administrativos (`requireAdministrator` em seu `app.manifest`). Essa elevação é indispensável para permitir que o gancho global de entrada e as janelas transparentes do WPF interajam sobre aplicativos elevados do sistema (como o Gerenciador de Tarefas, editores de registro e terminais administrativos), contornando as restrições impostas pelo **UIPI** (*User Interface Privilege Isolation*).

No entanto, executar ferramentas e comandos arbitrários herdando esse token de administrador configuraria uma vulnerabilidade de escalada de privilégios. Para assegurar isolamento total:
- **Execução com token primário do Explorer:** todo aplicativo favorito, navegador web, pasta ou conversor CLI acionado pela roda é iniciado através da função nativa `CreateProcessWithTokenW`, extraindo o token primário não elevado do processo do shell do usuário (`explorer.exe`):
  
  $$\text{OpenProcess}(\text{explorer.exe}) \longrightarrow \text{OpenProcessToken} \longrightarrow \text{DuplicateTokenEx} \longrightarrow \text{CreateProcessWithTokenW}$$
  
  O processo filho resultante recebe estritamente o nível de integridade Médio (`S-1-16-8192`), destituído de quaisquer privilégios administrativos.
- **Validação estrita de caminhos (`TrustedPath`):** todas as strings de alvo são validadas e normalizadas para seus caminhos canônicos absolutos antes de qualquer execução. O validador rejeita tentativas de travessia de diretório (`..`), identificadores de fluxos de dados alternados NTFS (*Alternate Data Streams* `:`), caracteres de controle e nomes reservados do MS-DOS (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`).

### 3. Motor de inferência contextual por predicados
Ao abrir a roda, o sistema inspeciona dinamicamente as características do elemento localizado sob as coordenadas do cursor $(x, y)$:
- A resolução de janelas utiliza `WindowFromPoint` e varredura na árvore de nós via `GetAncestor` e `GetGUIThreadInfo`.
- Em janelas do Windows Explorer, o serviço consulta o catálogo de instâncias COM (`IShellWindows`) para extrair a lista exata de arquivos selecionados ou o diretório aberto no momento.
- A composição de menus personalizados avalia em tempo real um predicado booleano composto:
  
  $$P(\text{alvo}) = C_{\text{processo}} \land C_{\text{título}} \land C_{\text{extensão}} \land C_{\text{classe}} \land C_{\text{contexto}}$$
  
  permitindo sobreposições específicas de menu para editores de código, navegadores ou tipos de arquivos individuais.

### 4. Empacotamento industrial e integridade de distribuição
- O instalador oficial é gerado pelo compilador **Inno Setup 6** (`RadialFlow.iss`) com algoritmo de compressão sólida LZMA2 de nível máximo (`lzma2/max`).
- Os binários são instalados no diretório protegido do sistema (`%ProgramFiles%\RadialFlow`), prevenindo modificação do executável por processos executados sob contas de usuário padrão.
- O pipeline de automação em PowerShell (`publish.ps1`) compila a suíte com o .NET SDK, verifica a consistência da versão do manifesto contra a do compilador e gera manifestos de integridade criptográfica `SHA256SUMS.txt`.

---

## Recursos

- **Acionamento duplo:** ativação intuitiva por microarrasto do botão direito ou por atalho de teclado global configurável (ex.: `Ctrl+Alt+Space`).
- **Contextos nativos dinâmicos:**
  - **Arquivos e pastas:** compactação ZIP segura, conversão de formatos com reconhecimento de ferramentas locais, cópia de caminho canônico e renomeação.
  - **Barra de título:** controle geométrico de janelas (maximizar, minimizar, encaixar à esquerda/direita, transparência contínua e fixação sempre no topo).
  - **Campos de texto:** operações de colagem, tradução, busca rápida, formatação e limpeza segura.
  - **Área de trabalho e espaço livre:** inicialização de aplicativos favoritos e histórico da área de transferência.
- **Motor de conversão inteligente:** integração automática com ferramentas CLI pré-instaladas (FFmpeg, ImageMagick, Pandoc, LibreOffice), executando pipelines de áudio, vídeo, imagem e documentos em diretórios temporários isolados com encerramento forçado após tempo limite.
- **Navegação acessível:** suporte completo a navegação por teclado durante a exibição da roda (teclas `1` a `8` por fatia horária, setas direcionais, `Enter` para confirmação e `Esc` para cancelamento imediato).
- **Temas de interface:** predefinições visuais (*Padrão*, *Compacto*, *Amplo* e *Alto Contraste* em superfícies sólidas) respeitando o modo claro/escuro nativo do Windows.

---

## Privacidade e segurança

- **Execução estritamente local:** o RadialFlow opera inteiramente na máquina do usuário, sem rotinas de telemetria, análise de uso ou envio de dados para a internet.
- **Proteção contra escalada de privilégios:** toda inicialização de programas é destituída de elevação através do `UnelevatedLauncher`, evitando que arquivos de configuração maliciosos executem binários como administrador.
- **Manipulação segura de arquivos compactados:** a extração de pacotes ZIP valida as datas dos cabeçalhos, bloqueia substituições acidentais de arquivos preexistentes e previne ataques de extração fora do diretório de destino (*Zip Slip*).

---

## Estrutura do projeto

```text
RadialFlow/
├── .github/
│   ├── ISSUE_TEMPLATE/
│   │   ├── bug_report.yml          # Formulário padronizado para relato de bugs
│   │   ├── config.yml              # Configuração de issues e canal de segurança
│   │   └── feature_request.yml     # Formulário padronizado para sugestões
│   ├── workflows/
│   │   ├── ci.yml                  # Esteira de restauração, build e testes automatizados
│   │   └── release.yml             # Compilação e publicação de releases no GitHub
│   ├── CODEOWNERS                  # Definição de propriedade de código (@PedroBosa)
│   ├── dependabot.yml              # Atualização semanal de dependências NuGet e GitHub Actions
│   ├── FUNDING.yml                 # Links de patrocínio e apoio comunitário
│   └── PULL_REQUEST_TEMPLATE.md    # Checklist de verificação para pull requests
├── src/
│   └── RadialFlow/
│       ├── Assets/                 # Ícones da aplicação (.ico) e recursos gráficos
│       ├── Config/                 # Serialização JSON de configurações, validações e perfis
│       ├── Context/                # Identificação de contexto, janelas e arquivos no Explorer
│       ├── Infrastructure/         # P/Invoke Win32, wrappers de SafeHandle e logging local
│       ├── Input/                  # Serviços de gancho global de mouse (WH_MOUSE_LL) e teclado
│       ├── Resources/              # Recursos de internacionalização e dicionários de texto
│       ├── Security/               # Validação de TrustedPath e integridade de executáveis
│       ├── Services/               # UnelevatedLauncher, conversões CLI e compactação ZIP
│       ├── UI/                     # Janelas WPF, controles radiais, temas e painel de ajustes
│       ├── App.xaml                # Ponto de entrada XAML da aplicação WPF
│       ├── app.manifest            # Manifesto Win32 solicitando requireAdministrator
│       └── RadialFlow.csproj       # Projeto C# configurado com .NET 10 e análise estrita
├── tests/
│   └── RadialFlow.Tests/           # Suíte de testes unitários automatizados com xUnit
├── tools/
│   └── IconGenerator/              # Utilitário para geração de ícones a partir de vetores
├── .editorconfig                   # Regras estritas de estilo C# e políticas de análise
├── .gitattributes                  # Normalização de quebras de linha e arquivos de script
├── .gitignore                      # Exclusão de artefatos de build (bin, obj, dist) e logs
├── CHANGELOG.md                    # Histórico detalhado de versões seguindo Keep a Changelog
├── CODE_OF_CONDUCT.md              # Código de conduta Contributor Covenant 2.1
├── CONTRIBUTING.md                 # Guia de contribuição e convenções de compilação
├── Directory.Build.props           # Metadados de empacotamento comuns e flags estritas do compilador
├── global.json                     # Fixação da versão do SDK .NET
├── LICENSE                         # Licença permissiva MIT
├── publish.ps1                     # Script de automação de compilação, testes e release
├── RadialFlow.iss                  # Script de compilação do instalador Inno Setup 6
├── RadialFlow.sln                  # Arquivo de solução do Visual Studio
├── README.md                       # Documentação técnica central do repositório
└── SECURITY.md                     # Diretrizes de segurança e canal de reporte confidencial
```

---

## Primeiros passos

### Pré-requisitos
- Sistema operacional Windows 10 (compilação 1809 ou superior) ou Windows 11 (64 bits).
- .NET 10 SDK instalado (verifique com `dotnet --version`).
- PowerShell 5.1 ou superior.

### Instalação e execução
```bash
git clone https://github.com/PedroBosa/RadialFlow.git
cd RadialFlow
dotnet run --project src/RadialFlow
```

### Build e verificação
```bash
dotnet build -c Release
dotnet test -c Release
```

Para gerar os pacotes de distribuição portáteis e o instalador oficial via Inno Setup:
```powershell
.\publish.ps1
```
Os artefatos compilados e o arquivo de assinaturas `SHA256SUMS.txt` serão gerados no diretório `dist/`.

---

## Compatibilidade

| Versão do Sistema Operacional | Arquitetura | Nível de Suporte |
|---|---|---|
| Windows 11 (21H2, 22H2, 23H2, 24H2) | x64 / ARM64 (emulado) | Suporte integral nativo; integração com temas escuros e snap layouts. |
| Windows 10 (1809, 20H2, 21H2, 22H2) | x64 | Suporte integral nativo. |
| Windows 8.1 e anteriores | x64 / x86 | Não suportado (recursos de API Win32 ausentes). |

---

## Stack e tecnologias

| Tecnologia | Versão | Função na arquitetura |
|---|---|---|
| .NET | 10.0 | Plataforma de execução gerenciada de alta performance |
| C# | 12 / latest | Linguagem principal com recursos modernos de tipagem estrita e pattern matching |
| WPF | Nativo | Renderização de interface acelerada por hardware com suporte a transparência |
| Win32 API | P/Invoke | Ganchos de baixo nível (`WH_MOUSE_LL`), manipulação de processos e tokens de segurança |
| xUnit | 2.9.2 | Framework de testes unitários automatizados |
| Inno Setup | 6.x | Sistema de geração de instalador de alta compressão (LZMA2) |

---

## Limitações conhecidas

- **Elevação administrativa obrigatória:** devido à necessidade de interceptar entradas de forma consistente e sobrepor janelas do sistema sem bloqueios pelo UIPI, o RadialFlow solicita privilégios de administrador em sua inicialização.
- **Filtro Windows SmartScreen:** os artefatos compilados são distribuídos sem assinatura digital comercial paga (certificado EV de alto custo). Em novas versões, o Windows SmartScreen pode exibir o alerta "O Windows protegeu o computador", exigindo a confirmação manual do usuário via "Mais informações" $\rightarrow$ "Executar assim mesmo". A integridade de qualquer download deve ser conferida contra os hashes do arquivo `SHA256SUMS.txt`.
- **Jogos e aplicativos com proteções em kernel (Anti-Cheat):** jogos eletrônicos que utilizam drivers anti-cheat em nível de kernel (como Riot Vanguard ou BattlEye) podem suprimir ganchos de mouse de usuário (`WH_MOUSE_LL`). Nesses ambientes, recomenda-se adicionar o executável do jogo à lista negra (*Blacklist*) nas configurações do RadialFlow ou utilizar o atalho global de teclado.

---

## Contribuindo

Contribuições que fortaleçam a segurança de isolamento de processos ou aprimorem o desempenho dos ganchos de entrada são bem-vindas. Consulte o guia detalhado em [`CONTRIBUTING.md`](CONTRIBUTING.md) antes de submeter alterações. Relatos de vulnerabilidades de segurança devem seguir a política descrita em [`SECURITY.md`](SECURITY.md).

---

## Licença

Distribuído sob a licença MIT. Consulte [`LICENSE`](LICENSE) para mais informações.

Desenvolvido por [Pedro Bosa](https://github.com/PedroBosa).
