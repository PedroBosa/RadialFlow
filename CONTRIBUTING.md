# Guia de Contribuição

Agradecemos o interesse em contribuir com o **RadialFlow**. Este documento estabelece as diretrizes para desenvolvimento, estilo de código, testes e envio de contribuições.

---

## 1. Princípios de Desenvolvimento

1. **Segurança em Primeiro Lugar (Isolamento UIPI):** O RadialFlow executa com privilégios administrativos para interagir sobre qualquer janela do Windows, mas NUNCA deve iniciar processos arbitrários como administrador. Toda criação de processo filho deve obrigatoriamente utilizar o `UnelevatedLauncher` e passar pela validação de integridade do `TrustedPath`.
2. **Performance e Responsividade de Entrada:** O gancho de mouse (`MouseHookService`) roda em uma thread STA dedicada e isolada da UI. Nenhuma operação de entrada de baixo nível (`WH_MOUSE_LL`) pode conter código bloqueante (I/O de disco, consultas COM ou operações pesadas).
3. **Qualidade e Análise Estrita:** O compilador está configurado com `TreatWarningsAsErrors=true` e `AnalysisMode=Recommended`. Todo aviso é tratado como erro impeditivo de compilação.

---

## 2. Pré-requisitos do Ambiente

- **Sistema Operacional:** Windows 10 (compilação 1809+) ou Windows 11.
- **.NET SDK:** Versão 10.0 ou superior (verifique com `dotnet --version`).
- **Visual Studio 2022** (com a carga de trabalho de desenvolvimento para desktop .NET) ou **VS Code** com extensão C# Dev Kit.
- **Inno Setup 6** (opcional, necessário apenas para compilar o instalador `.exe` via `publish.ps1`).

---

## 3. Configuração Local e Compilação

Clone o repositório:

```bash
git clone https://github.com/PedroBosa/RadialFlow.git
cd RadialFlow
```

Restaure e compile a solução em modo Release:

```bash
dotnet restore RadialFlow.sln
dotnet build RadialFlow.sln -c Release
```

---

## 4. Execução de Testes Automatizados

Antes de abrir um Pull Request, execute a suíte de testes unitários:

```bash
dotnet test RadialFlow.sln -c Release
```

Todos os testes devem passar com sucesso.

---

## 5. Padrão de Commits

Este projeto adere à convenção [Conventional Commits](https://www.conventionalcommits.org/pt-br/v1.0.0/). Toda mensagem de commit deve seguir a estrutura:

```text
<tipo>(<escopo opcional>): <descrição em minúsculas>
```

Tipos permitidos:

| Tipo | Descrição |
|---|---|
| `feat` | Nova funcionalidade para o usuário ou editor |
| `fix` | Correção de defeito funcional, ótico ou de compilação |
| `docs` | Alterações exclusivas na documentação |
| `refactor` | Refatoração de código que não altera comportamento |
| `perf` | Melhoria de desempenho em hooks ou renderização WPF |
| `chore` | Manutenção de build, dependências ou governança |

Exemplos:
- `feat(menu): adicionar predicado por hash de executável`
- `fix(hook): prevenir perda de foco em janelas UWP suspensas`
- `docs(readme): atualizar tabela de compatibilidade com Windows 11 24H2`

---

## 6. Fluxo de Envio de Pull Requests

1. Crie uma branch específica a partir da `main`:
   ```bash
   git checkout -b feat/nome-da-funcionalidade
   ```
2. Realize as alterações mantendo commits atômicos e descritivos.
3. Execute as verificações locais (`dotnet build` e `dotnet test`).
4. Envie a branch para o seu fork ou para o repositório remoto:
   ```bash
   git push origin feat/nome-da-funcionalidade
   ```
5. Abra o Pull Request no GitHub preenchendo todos os itens do template obrigatório.

---

## 7. Reporte de Vulnerabilidades

Caso identifique uma falha ou risco de segurança, não abra uma issue pública. Siga as instruções descritas em [`SECURITY.md`](SECURITY.md).
