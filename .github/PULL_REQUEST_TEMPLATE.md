## Descrição da Mudança

Breve resumo técnico das alterações propostas, relacionando-as ao problema original ou issue associada.

Issue relacionada: Fixes #

## Tipo de Alteração

- [ ] Correção de bug (`fix`)
- [ ] Nova funcionalidade (`feat`)
- [ ] Refatoração ou melhoria técnica (`refactor`)
- [ ] Ajuste de documentação (`docs`)
- [ ] Configuração, CI/CD ou dependências (`chore`)

## Checklist de Verificação

- [ ] O código adere às convenções do projeto e ao `.editorconfig` (sem avisos de compilação ou do compilador C#).
- [ ] `dotnet build RadialFlow.sln -c Release` executa com zero erros e zero avisos (`TreatWarningsAsErrors=true`).
- [ ] `dotnet test RadialFlow.sln` passa em todos os testes unitários.
- [ ] Todas as chamadas de inicialização de processos respeitam a fronteira de segurança do `UnelevatedLauncher` e `TrustedPath`.
- [ ] A documentação e/ou o `CHANGELOG.md` foram atualizados quando aplicável.
- [ ] As mensagens de commit seguem a convenção [Conventional Commits](https://www.conventionalcommits.org/).
