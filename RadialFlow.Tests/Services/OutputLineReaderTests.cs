using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RadialFlow.Services;
using Xunit;

namespace RadialFlow.Tests;

public sealed class OutputLineReaderTests
{
    private static List<string> Read(byte[] data, int maxLineLength = OutputLineReader.DefaultMaxLineLength,
        int bufferSize = OutputLineReader.DefaultBufferSize, Action<string>? also = null)
    {
        var lines = new List<string>();
        using var stream = new MemoryStream(data);
        OutputLineReader.Pump(stream, line =>
        {
            lines.Add(line);
            also?.Invoke(line);
        }, maxLineLength, bufferSize);
        return lines;
    }

    private static List<string> Read(string text, int maxLineLength = OutputLineReader.DefaultMaxLineLength,
        int bufferSize = OutputLineReader.DefaultBufferSize) =>
        Read(Encoding.UTF8.GetBytes(text), maxLineLength, bufferSize);

    // ------------------------------------------------------------------
    // Terminadores
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("a\nb\nc\n", new[] { "a", "b", "c" })]
    [InlineData("a\r\nb\r\nc\r\n", new[] { "a", "b", "c" })]
    [InlineData("a\rb\rc\r", new[] { "a", "b", "c" })]            // progresso do ffmpeg
    [InlineData("a\nb\r\nc\rd", new[] { "a", "b", "c", "d" })]     // misturado, sem terminador final
    [InlineData("sem terminador", new[] { "sem terminador" })]
    [InlineData("a\n\nb", new[] { "a", "", "b" })]                  // linha vazia é entregue
    [InlineData("a\r\r\nb", new[] { "a", "", "b" })]                // "\r" + "\r\n" = duas quebras
    [InlineData("a\n\rb", new[] { "a", "", "b" })]                  // "\n\r" NÃO é um terminador só
    public void Pump_DivideNasLinhasCertas(string input, string[] expected)
    {
        Assert.Equal(expected, Read(input));
    }

    [Fact]
    public void Pump_FluxoVazio_NaoChamaOCallback()
    {
        Assert.Empty(Read(Array.Empty<byte>()));
    }

    [Fact]
    public void Pump_SoUmTerminador_EntregaUmaLinhaVazia()
    {
        Assert.Equal(new[] { "" }, Read("\n"));
        Assert.Equal(new[] { "" }, Read("\r\n"));
    }

    [Fact]
    public void Pump_CrLfPartidoEntreDuasLeituras_NaoGeraLinhaExtra()
    {
        // bufferSize = 1: "\r" e "\n" chegam em leituras diferentes.
        Assert.Equal(new[] { "a", "b" }, Read("a\r\nb\r\n", bufferSize: 1));
    }

    // ------------------------------------------------------------------
    // UTF-8
    // ------------------------------------------------------------------

    [Fact]
    public void Pump_CaractereMultibytePartidoEntreLeituras_SaiInteiro()
    {
        const string text = "ação: 日本語 😀 fim";

        // Um byte por leitura: todo caractere multibyte é partido.
        Assert.Equal(new[] { text }, Read(text, bufferSize: 1));
        Assert.Equal(new[] { text }, Read(text, bufferSize: 3));
    }

    [Fact]
    public void Pump_BytesInvalidos_ViramCaractereDeSubstituicaoSemLancar()
    {
        byte[] data = [(byte)'o', (byte)'k', 0xFF, 0xFE, (byte)'!', (byte)'\n'];

        var lines = Read(data);

        Assert.Single(lines);
        Assert.StartsWith("ok", lines[0]);
        Assert.EndsWith("!", lines[0]);
        Assert.Contains('\uFFFD', lines[0]);
    }

    [Fact]
    public void Pump_SequenciaUtf8IncompletaNoFim_NaoPerdeAsLinhas()
    {
        byte[] data = [.. Encoding.UTF8.GetBytes("linha\nresto"), 0xE6];   // 0xE6 abre um caractere de 3 bytes que nunca termina

        var lines = Read(data);

        Assert.Equal("linha", lines[0]);
        Assert.StartsWith("resto", lines[1]);
    }

    [Fact]
    public void Pump_NaoTrataBomComoTexto_DeixaOCaractereIntacto()
    {
        // O decoder não consome BOM (pipes de processos não têm); se vier, é só um caractere.
        var lines = Read([0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n']);

        Assert.Single(lines);
        Assert.EndsWith("a", lines[0]);
    }

    // ------------------------------------------------------------------
    // Limite de tamanho (memória limitada)
    // ------------------------------------------------------------------

    [Fact]
    public void Pump_LinhaSemTerminadorMaiorQueOLimite_ECortadaEmPedacos()
    {
        Assert.Equal(new[] { "aaaa", "aaaa", "aa" }, Read(new string('a', 10), maxLineLength: 4));
    }

    [Fact]
    public void Pump_LinhaComExatamenteOLimite_NaoGeraLinhaVaziaSobrando()
    {
        Assert.Equal(new[] { "abcd", "efgh" }, Read("abcd\nefgh\n", maxLineLength: 4));
        Assert.Equal(new[] { "abcd" }, Read("abcd", maxLineLength: 4));
    }

    [Fact]
    public void Pump_SaidaBinariaGigante_NuncaGeraLinhaMaiorQueOLimite()
    {
        var lines = Read(new string('x', 200_000), maxLineLength: 1000, bufferSize: 512);

        Assert.Equal(200, lines.Count);
        Assert.All(lines, l => Assert.True(l.Length <= 1000));
    }

    // ------------------------------------------------------------------
    // Robustez
    // ------------------------------------------------------------------

    [Fact]
    public void Pump_CallbackQueLanca_NaoInterrompeALeitura()
    {
        var seen = new List<string>();

        var lines = Read(Encoding.UTF8.GetBytes("a\nb\nc\n"), also: line =>
        {
            seen.Add(line);
            if (line == "a") throw new InvalidOperationException("falha proposital");
        });

        Assert.Equal(new[] { "a", "b", "c" }, lines);   // o resto continua sendo lido
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void Pump_ParametrosInvalidos_Lancam()
    {
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentNullException>(() => OutputLineReader.Pump(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => OutputLineReader.Pump(stream, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputLineReader.Pump(stream, _ => { }, maxLineLength: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputLineReader.Pump(stream, _ => { }, bufferSize: 0));
    }

    [Fact]
    public void Pump_ExcecaoDeLeituraDoStream_PropagaParaOChamador()
    {
        // O launcher captura essa exceção (pipe quebrado) no nível da thread de leitura.
        using var stream = new ThrowingStream();

        Assert.Throws<IOException>(() => OutputLineReader.Pump(stream, _ => { }));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("pipe quebrado");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
