using Alento.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Painel;

public record ResumoMes(
    int SessoesRealizadas,
    int SessoesPrevistas,
    int Faltas,
    int Cancelamentos,
    decimal Recebido,
    decimal AReceber,
    decimal Previsto,
    int Confirmadas,
    int LembretesEnviados,
    decimal CustoLembretes)
{
    public decimal TaxaFaltas => SessoesRealizadas + Faltas == 0 ? 0 : (decimal)Faltas / (SessoesRealizadas + Faltas);
}

public record ProgressoAtivacao(bool Horarios, bool ChavePix, bool Paciente, bool Sessao, bool Assinatura)
{
    public int Feitos => new[] { Horarios, ChavePix, Paciente, Sessao, Assinatura }.Count(x => x);
    public int Percentual => Feitos * 100 / 5;
}

public class PainelService(IDbContextFactory<AppDbContext> dbf)
{
    public async Task<ResumoMes> ResumoAsync(int ano, int mes, TimeZoneInfo tz)
    {
        var de = Fuso.ParaUtc(new DateOnly(ano, mes, 1), TimeOnly.MinValue, tz);
        var ate = Fuso.ParaUtc(new DateOnly(ano, mes, 1).AddMonths(1), TimeOnly.MinValue, tz);

        await using var db = await dbf.CreateDbContextAsync();
        var ags = await db.Agendamentos.AsNoTracking()
            .Where(a => a.Inicio >= de && a.Inicio < ate)
            .Select(a => new { a.Status, a.Valor, a.ConfirmadoEm })
            .ToListAsync();

        var cobrancas = await db.Cobrancas.AsNoTracking()
            .Where(c => c.Agendamento!.Inicio >= de && c.Agendamento.Inicio < ate && c.Status != StatusCobranca.Cancelada)
            .Select(c => new { c.Status, c.Valor }).ToListAsync();

        var msgs = await db.Mensagens.AsNoTracking()
            .Where(m => m.CriadoEm >= de && m.CriadoEm < ate && m.Status != StatusMensagem.Falhou)
            .Select(m => m.Custo).ToListAsync();

        var ativos = ags.Where(a => a.Status is StatusAgendamento.Agendado or StatusAgendamento.Confirmado).ToList();
        return new ResumoMes(
            SessoesRealizadas: ags.Count(a => a.Status == StatusAgendamento.Realizado),
            SessoesPrevistas: ativos.Count,
            Faltas: ags.Count(a => a.Status == StatusAgendamento.Faltou),
            Cancelamentos: ags.Count(a => a.Status == StatusAgendamento.Cancelado),
            Recebido: cobrancas.Where(c => c.Status == StatusCobranca.Paga).Sum(c => c.Valor),
            AReceber: cobrancas.Where(c => c.Status == StatusCobranca.Pendente).Sum(c => c.Valor),
            Previsto: ativos.Sum(a => a.Valor),
            Confirmadas: ags.Count(a => a.ConfirmadoEm != null),
            LembretesEnviados: msgs.Count,
            CustoLembretes: msgs.Sum());
    }

    /// <summary>Checklist de ativação: quem completa os primeiros passos no teste é quem assina.</summary>
    public async Task<ProgressoAtivacao> ProgressoAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == db.TenantAtual);
        return new ProgressoAtivacao(
            Horarios: await db.Disponibilidades.AnyAsync(),
            ChavePix: !string.IsNullOrWhiteSpace(tenant.ChavePix),
            Paciente: await db.Pacientes.AnyAsync(),
            Sessao: await db.Agendamentos.AnyAsync(),
            Assinatura: tenant.StatusAssinatura == StatusAssinatura.Ativa);
    }

    /// <summary>Sessões passadas que ainda estão como agendado/confirmado: o psicólogo precisa dar baixa.</summary>
    public async Task<int> PendentesDeBaixaAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var agora = DateTime.UtcNow;
        return await db.Agendamentos.CountAsync(a => a.Fim < agora
            && (a.Status == StatusAgendamento.Agendado || a.Status == StatusAgendamento.Confirmado));
    }
}
