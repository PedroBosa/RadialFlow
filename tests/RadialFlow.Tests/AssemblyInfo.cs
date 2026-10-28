using Xunit;

// Os testes do UnelevatedLauncher criam processos reais e medem tempo de encerramento e contagem de
// handles do processo de teste. Classes de teste rodando em paralelo (criando/fechando arquivos e handles
// ao mesmo tempo) tornariam essas medições instáveis. A suíte é pequena; a execução em série custa segundos.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
