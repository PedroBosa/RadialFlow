# Política de segurança

## Versões suportadas

A versão **1.4.x** recebe correções de segurança. Versões anteriores não têm suporte.

## Como relatar

Relate vulnerabilidades **em privado**: na página do repositório, use **Security → Report a vulnerability**. Nunca
abra uma issue pública para um problema de segurança.

Inclua, se possível:

- a versão do RadialFlow e do Windows;
- os passos para reproduzir;
- o impacto esperado;
- uma prova de conceito, quando houver.

## Prazos

- Confirmação do relato em até **3 dias úteis**.
- Triagem e primeira avaliação em **7 dias**.
- Divulgação coordenada em até **90 dias**, ou antes se a correção for publicada.

O crédito pelo achado é opcional e combinado com quem reporta.

## Escopo

Problemas no aplicativo, no instalador e nos artefatos oficiais, incluindo:

- ZIP: datas inválidas, path traversal e sobrescrita indevida;
- importação de menus e validação de perfis e itens;
- validação de caminhos e de executáveis antes de executar ou abrir;
- download e verificação de hash dos conversores;
- limpeza de arquivos e pastas temporárias.

## Fora de escopo

- Falhas nos conversores de terceiros (FFmpeg, Pandoc, ImageMagick e LibreOffice): relate aos projetos originais.
- Avisos do SmartScreen e reputação de arquivos não assinados.
- Engenharia social.
- Acesso físico ou com privilégios de administrador na máquina.

## Artefatos não assinados

Os arquivos distribuídos **não têm assinatura digital**. Confira o `SHA256SUMS.txt` da release oficial para verificar
a integridade do download — isso garante que o arquivo não foi corrompido ou alterado, mas não a autoria. Veja também
a seção [Segurança](README.md#segurança) do README.
