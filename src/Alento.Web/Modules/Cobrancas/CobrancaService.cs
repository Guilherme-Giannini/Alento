using Alento.Web.Data;
using Alento.Web.Modules.Contas;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Alento.Web.Modules.Cobrancas;

public record ItemCobranca(
    Guid Id, Guid AgendamentoId, Guid PacienteId, string Paciente, string WhatsApp, DateTime SessaoUtc,
    decimal Valor, StatusCobranca Status, DateTime? PagoEm);

public record PixGerado(string CopiaECola, string QrCodeDataUri, decimal Valor);

public class CobrancaService(IDbContextFactory<AppDbContext> dbf, ContaService conta)
{
    public async Task<List<ItemCobranca>> ListarAsync(StatusCobranca? status = null, DateTime? deUtc = null, DateTime? ateUtc = null)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Cobrancas.AsNoTracking()
            .Where(c => (status == null || c.Status == status)
                        && (deUtc == null || c.Agendamento!.Inicio >= deUtc)
                        && (ateUtc == null || c.Agendamento!.Inicio < ateUtc))
            .OrderByDescending(c => c.Agendamento!.Inicio)
            .Select(c => new ItemCobranca(c.Id, c.AgendamentoId, c.Agendamento!.PacienteId, c.Agendamento.Paciente!.Nome,
                c.Agendamento.Paciente.WhatsApp, c.Agendamento.Inicio, c.Valor, c.Status, c.PagoEm))
            .ToListAsync();
    }

    public async Task<Guid> GerarParaAgendamentoAsync(Guid agendamentoId)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        var ag = await db.Agendamentos.Include(a => a.Cobranca).FirstAsync(a => a.Id == agendamentoId);
        if (ag.Cobranca is not null) return ag.Cobranca.Id;
        var c = new Cobranca { AgendamentoId = ag.Id, Valor = ag.Valor };
        db.Cobrancas.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    public async Task MarcarComoPagaAsync(Guid id, bool paga = true)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.Cobrancas.FirstAsync(x => x.Id == id);
        c.Status = paga ? StatusCobranca.Paga : StatusCobranca.Pendente;
        c.PagoEm = paga ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
    }

    public async Task CancelarAsync(Guid id)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.Cobrancas.FirstAsync(x => x.Id == id);
        c.Status = StatusCobranca.Cancelada;
        await db.SaveChangesAsync();
    }

    /// <summary>Pix copia-e-cola + QR Code com a chave do próprio psicólogo (sem gateway no MVP).</summary>
    public async Task<PixGerado> PixAsync(Guid cobrancaId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.Cobrancas.AsNoTracking().FirstAsync(x => x.Id == cobrancaId);
        var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == c.TenantId);
        if (string.IsNullOrWhiteSpace(t.ChavePix))
            throw new RegraNegocioException("Cadastre sua chave Pix em Configurações para gerar a cobrança.");

        var codigo = PixCopiaECola.Gerar(t.ChavePix, t.Nome, t.CidadePix ?? "", c.Valor, "ALENTO" + c.Id.ToString("N")[..12]);
        return new PixGerado(codigo, QrCode(codigo), c.Valor);
    }

    public static string QrCode(string conteudo)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(conteudo, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    public static string MensagemWhatsApp(string nomePaciente, string nomePsicologo, decimal valor, DateTime sessaoLocal, string pix) =>
        $"Olá, {Texto.PrimeiroNome(nomePaciente)}! Segue o Pix referente à sessão de {sessaoLocal:dd/MM} " +
        $"no valor de {Texto.Moeda(valor)}.\n\nPix copia e cola:\n{pix}\n\n{nomePsicologo}";
}
