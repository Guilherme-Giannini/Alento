using Alento.Web;
using Alento.Web.Data;
using Alento.Web.Modules.Cobrancas;
using Alento.Web.Modules.Contas;
using Alento.Web.Modules.Lembretes;

namespace Alento.Tests;

public class PixCopiaEColaTests
{
    [Fact]
    public void Crc16_ccitt_false_confere_com_valor_de_referencia()
    {
        // Vetor de teste clássico do CRC-16/CCITT-FALSE.
        Assert.Equal(0x29B1, PixCopiaECola.Crc16("123456789"));
    }

    [Fact]
    public void Codigo_gerado_tem_estrutura_emv_e_crc_valido()
    {
        var codigo = PixCopiaECola.Gerar("ana@exemplo.com", "Ana Souza", "São Paulo", 150m, "ALENTO123");

        Assert.StartsWith("000201", codigo);
        Assert.Contains("0014br.gov.bcb.pix", codigo);
        Assert.Contains("0115ana@exemplo.com", codigo);
        Assert.Contains("5406150.00", codigo);
        Assert.Contains("5303986", codigo);
        Assert.Contains("5802BR", codigo);
        Assert.Contains("5909ANA SOUZA", codigo);
        Assert.Contains("6009SAO PAULO", codigo);

        var semCrc = codigo[..^4];
        Assert.EndsWith("6304", semCrc);
        Assert.Equal(PixCopiaECola.Crc16(semCrc).ToString("X4"), codigo[^4..]);
    }

    [Fact]
    public void Sem_valor_o_campo_54_e_omitido()
    {
        Assert.DoesNotContain("54", PixCopiaECola.Gerar("+5511912345678", "Ana", "Recife", null).Split("5303986")[1][..2]);
    }

    [Theory]
    [InlineData("123.456.789-09", "12345678909")]
    [InlineData("12.345.678/0001-95", "12345678000195")]
    [InlineData("+55 (11) 91234-5678", "+5511912345678")]
    [InlineData("Ana@Exemplo.com", "ana@exemplo.com")]
    [InlineData("7d9f0335-8dcc-4054-9bf9-0dbd61d36906", "7d9f0335-8dcc-4054-9bf9-0dbd61d36906")]
    public void Normaliza_os_tipos_de_chave(string entrada, string esperado)
    {
        Assert.Equal(esperado, PixCopiaECola.NormalizarChave(entrada));
    }

    [Fact]
    public void Nome_longo_e_acentuado_e_truncado_sem_acento()
    {
        var codigo = PixCopiaECola.Gerar("x@y.com", "Maria Conceição de Albuquerque Figueiredo", "Florianópolis Capital", 1m);

        Assert.Contains("5925MARIA CONCEICAO DE ALBUQ", codigo);
        Assert.Contains("6015FLORIANOPOLIS C", codigo);
    }
}

public class RegrasAcessoTests
{
    private static readonly DateTime Agora = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(StatusAssinatura.Ativa, 0, NivelAcesso.Total)]
    [InlineData(StatusAssinatura.Teste, 3, NivelAcesso.Total)]
    [InlineData(StatusAssinatura.Teste, -1, NivelAcesso.Bloqueado)]
    [InlineData(StatusAssinatura.Inadimplente, 0, NivelAcesso.Bloqueado)]
    [InlineData(StatusAssinatura.Cancelada, 0, NivelAcesso.SomenteLeitura)]
    public void Nivel_de_acesso_segue_o_status_da_assinatura(StatusAssinatura status, int diasTeste, NivelAcesso esperado)
    {
        var t = new Tenant { StatusAssinatura = status, TesteTerminaEm = Agora.AddDays(diasTeste) };

        Assert.Equal(esperado, RegrasAcesso.Calcular(t, Agora));
    }

    [Fact]
    public void Dias_restantes_arredonda_para_cima()
    {
        var t = new Tenant { StatusAssinatura = StatusAssinatura.Teste, TesteTerminaEm = Agora.AddHours(30) };

        Assert.Equal(2, RegrasAcesso.DiasRestantesTeste(t, Agora));
    }

    [Fact]
    public void Link_publico_so_funciona_com_acesso_total()
    {
        Assert.False(RegrasAcesso.LinkPublicoAtivo(new Tenant { StatusAssinatura = StatusAssinatura.Cancelada }, Agora));
        Assert.True(RegrasAcesso.LinkPublicoAtivo(new Tenant { StatusAssinatura = StatusAssinatura.Ativa }, Agora));
    }
}

public class TextoTests
{
    [Theory]
    [InlineData("(11) 91234-5678", "5511912345678")]
    [InlineData("11 3456-7890", "551134567890")]
    [InlineData("+55 11 91234-5678", "5511912345678")]
    public void Normaliza_whatsapp_para_e164(string entrada, string esperado) =>
        Assert.Equal(esperado, Texto.NormalizarWhatsApp(entrada));

    [Theory]
    [InlineData("1234")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejeita_whatsapp_invalido(string? numero) => Assert.False(Texto.WhatsAppValido(numero));

    [Theory]
    [InlineData("Dra. Ana Souza", "dra-ana-souza")]
    [InlineData("  João   Ávila  ", "joao-avila")]
    public void Gera_slug_sem_acentos(string nome, string esperado) => Assert.Equal(esperado, Texto.Slug(nome));

    [Fact]
    public void Descricao_do_lembrete_nao_menciona_terapia()
    {
        var tenant = new Tenant { FusoHorario = "America/Sao_Paulo" };
        var quando = LembreteService.DescreverQuando(DateTime.UtcNow.AddDays(1), tenant);

        Assert.StartsWith("amanhã", quando);
        Assert.DoesNotContain("terapia", quando, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assinatura_do_webhook_da_meta_e_validada()
    {
        const string segredo = "segredo";
        const string corpo = "{\"a\":1}";
        var hmac = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(segredo), System.Text.Encoding.UTF8.GetBytes(corpo)));

        Assert.True(WhatsAppClient.AssinaturaValida(segredo, corpo, "sha256=" + hmac));
        Assert.False(WhatsAppClient.AssinaturaValida(segredo, corpo, "sha256=00"));
        Assert.False(WhatsAppClient.AssinaturaValida(segredo, corpo, null));
    }
}
