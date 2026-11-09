# Política de Segurança

A integridade do aplicativo, o isolamento de privilégios de interface (UIPI) e a proteção do usuário no sistema operacional Windows são pilares fundamentais do **RadialFlow**. Como a aplicação executa com privilégios de administrador para sobrepor janelas do sistema, o isolamento dos processos filhos (`UnelevatedLauncher`) e a validação estrita de caminhos (`TrustedPath`) são prioridades críticas de segurança.

---

## Versões com Suporte Ativo

A tabela abaixo descreve as versões do RadialFlow que recebem atualizações de segurança:

| Versão | Suporte Ativo | Observações |
|---|---|---|
| `1.5.x` | Sim | Versão ativa de desenvolvimento na branch `main`. |
| `< 1.5.0` | Não | Versões anteriores não suportadas. |

---

## Como Relatar uma Vulnerabilidade

Se você identificou uma potencial vulnerabilidade de segurança no código, nas rotinas de elevação, nos ganchos de entrada ou no instalador do RadialFlow, por favor siga este procedimento:

1. **Não abra uma issue pública.** Issues públicas expõem potenciais falhas antes que uma correção esteja disponível.
2. Acesse a funcionalidade de reporte confidencial do repositório no GitHub:
   [**Reportar Vulnerabilidade Privada**](https://github.com/PedroBosa/RadialFlow/security/advisories/new)
3. Forneça o máximo de detalhes possível, incluindo:
   - Versão do RadialFlow e compilação do Windows testadas.
   - Descrição detalhada do vetor de ataque ou brecha de escalada de privilégios.
   - Passos passo a passo ou script de prova de conceito (PoC) para reproduzir o problema.
   - Análise de impacto e permissões requeridas.

---

## Processo de Resposta

- **Confirmação inicial:** Faremos a confirmação de recebimento do seu relato em até 3 dias úteis.
- **Triagem técnica:** Avaliaremos a gravidade e o escopo da falha em até 7 dias úteis.
- **Correção e publicação:** Uma versão corrigida será preparada e disponibilizada na branch `main` e publicada nas Releases oficiais do GitHub após validação da suíte de testes.
- **Divulgação coordenada:** A vulnerabilidade será divulgada publicamente com os créditos devidos ao pesquisador somente após a disponibilização da atualização corretiva.

---

## Escopo de Segurança

Problemas cobertos pelo programa de segurança incluem:
- **Escala de privilégios:** falhas na fronteira do `UnelevatedLauncher` que permitam a execução de binários externos ou comandos arbitrários com elevação administrativa.
- **Injeção e travessia de caminhos:** contorno de validações do `TrustedPath` (Alternate Data Streams, path traversal `..` ou nomes DOS reservados).
- **Manipulação de arquivos temporários:** vulnerabilidades de sobrescrita insegura em descompactações ZIP ou rotinas de conversão.
- **Integridade de artefatos:** verificação de hashes SHA-256 no download de dependências auxiliares.

---

## Fora de Escopo

- Falhas internas em ferramentas de terceiros (FFmpeg, ImageMagick, Pandoc ou LibreOffice) que devem ser reportadas diretamente aos respectivos mantenedores.
- Notificações de filtros de reputação do Windows Defender SmartScreen para binários que não possuem assinatura digital paga com certificado EV.
- Cenários que exijam acesso físico prévio ou comprometimento da conta de administrador da máquina.
