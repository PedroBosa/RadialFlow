using System;
using System.Threading;
using RadialFlow.Infrastructure;
using Xunit;

namespace RadialFlow.Tests;

public sealed class SingleInstanceTests
{
    /// <summary>Nome único por teste: não colide com o app real nem com outros testes.</summary>
    private static string UniqueName() => $"RadialFlow.Test.{Guid.NewGuid():N}";

    [Fact]
    public void PrimeiraInstancia_ObtemATrava()
    {
        using SingleInstance? first = SingleInstance.TryAcquire(UniqueName());

        Assert.NotNull(first);
        Assert.True(first!.IsGuarded);
    }

    [Fact]
    public void SegundaInstancia_NaoObtemATrava()
    {
        string name = UniqueName();
        using SingleInstance? first = SingleInstance.TryAcquire(name);

        SingleInstance? second = SingleInstance.TryAcquire(name);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void DepoisDeLiberar_OutraInstanciaPodeAssumir()
    {
        string name = UniqueName();
        SingleInstance first = SingleInstance.TryAcquire(name)!;
        first.Dispose();

        using SingleInstance? next = SingleInstance.TryAcquire(name);

        Assert.NotNull(next);   // o nome some quando o primeiro termina (sem mutex "abandonado")
    }

    [Fact]
    public void NomesDiferentes_NaoSeAfetam()
    {
        using SingleInstance? a = SingleInstance.TryAcquire(UniqueName());
        using SingleInstance? b = SingleInstance.TryAcquire(UniqueName());

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void Sinal_ChegaNaPrimeiraInstancia()
    {
        string name = UniqueName();
        using SingleInstance first = SingleInstance.TryAcquire(name)!;
        using var received = new ManualResetEventSlim(false);
        first.StartListening(received.Set);

        bool delivered = SingleInstance.SignalExisting(name);

        Assert.True(delivered);
        Assert.True(received.Wait(TimeSpan.FromSeconds(5)), "a primeira instância não recebeu o sinal");
    }

    [Fact]
    public void VariosSinais_SaoTodosEntregues()
    {
        string name = UniqueName();
        using SingleInstance first = SingleInstance.TryAcquire(name)!;
        int count = 0;
        using var three = new CountdownEvent(3);
        first.StartListening(() =>
        {
            Interlocked.Increment(ref count);
            three.Signal();
        });

        for (int i = 0; i < 3; i++)
        {
            Assert.True(SingleInstance.SignalExisting(name));
            Thread.Sleep(80);   // evento de reset automático: sinais colados se fundiriam em um só
        }

        Assert.True(three.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, Volatile.Read(ref count));
    }

    [Fact]
    public void CallbackQueLanca_NaoParaAEscuta()
    {
        string name = UniqueName();
        using SingleInstance first = SingleInstance.TryAcquire(name)!;
        int calls = 0;
        using var second = new ManualResetEventSlim(false);
        first.StartListening(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("falha proposital");
            second.Set();
        });

        SingleInstance.SignalExisting(name);
        Thread.Sleep(200);
        SingleInstance.SignalExisting(name);

        Assert.True(second.Wait(TimeSpan.FromSeconds(5)), "a escuta deveria continuar depois de uma exceção");
    }

    [Fact]
    public void SinalSemNenhumaInstancia_DevolveFalseSemLancar()
    {
        Assert.False(SingleInstance.SignalExisting(UniqueName()));
    }

    [Fact]
    public void SegundaCopiaQueChegaJunto_SempreConsegueSinalizar()
    {
        // Regressão: a trava era criada antes do evento. Uma segunda cópia que chegasse nesse intervalo via a trava,
        // não achava o evento e o pedido de abrir as Configurações se perdia (WaitHandleCannotBeOpenedException).
        for (int i = 0; i < 50; i++)
        {
            string name = UniqueName();
            using SingleInstance first = SingleInstance.TryAcquire(name)!;

            Assert.Null(SingleInstance.TryAcquire(name));        // a segunda cópia vê a trava...
            Assert.True(SingleInstance.SignalExisting(name));    // ...e o evento já existe
        }
    }

    [Fact]
    public void SinalEnviadoAntesDeComecarAOuvir_NaoSePerde()
    {
        // A primeira cópia só começa a ouvir depois de iniciar tudo; um pedido que chegue antes fica guardado.
        string name = UniqueName();
        using SingleInstance first = SingleInstance.TryAcquire(name)!;
        Assert.True(SingleInstance.SignalExisting(name));

        using var received = new ManualResetEventSlim(false);
        first.StartListening(received.Set);

        Assert.True(received.Wait(TimeSpan.FromSeconds(5)), "o pedido feito antes da escuta deveria ser entregue");
    }

    [Fact]
    public void DepoisDoDispose_NaoHaMaisEntrega()
    {
        string name = UniqueName();
        SingleInstance first = SingleInstance.TryAcquire(name)!;
        int calls = 0;
        first.StartListening(() => Interlocked.Increment(ref calls));
        first.Dispose();

        Assert.False(SingleInstance.SignalExisting(name));
        Thread.Sleep(200);
        Assert.Equal(0, Volatile.Read(ref calls));
        first.Dispose();   // idempotente
    }

    [Fact]
    public void StartListening_ChamadoDuasVezes_NaoDuplicaAEntrega()
    {
        string name = UniqueName();
        using SingleInstance first = SingleInstance.TryAcquire(name)!;
        int calls = 0;
        first.StartListening(() => Interlocked.Increment(ref calls));
        first.StartListening(() => Interlocked.Increment(ref calls));

        SingleInstance.SignalExisting(name);
        Thread.Sleep(400);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NomeInvalido_Lanca(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => SingleInstance.TryAcquire(name!));
        Assert.ThrowsAny<ArgumentException>(() => SingleInstance.SignalExisting(name!));
    }
}
