using Alento.Web.Data;
using Alento.Web.Modules.Agenda;
using Alento.Web.Modules.Email;
using Alento.Web.Modules.Lembretes;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Infra;

public class Jobs(
    IDbContextFactory<AppDbContext> dbf,
    LembreteService lembretes,
    IEmailService email,
    IConfiguration cfg,
    ILogger<Jobs> log)
{
    public static void Registrar(IRecurringJobManager rj)
    {
        rj.AddOrUpdate<Jobs>("lembretes-whatsapp", j => j.EnviarLembretes(), "*/15 * * * *");
        rj.AddOrUpdate<Jobs>("gerar-recorrencias", j => j.GerarRecorrencias(), "0 6 * * *");
        rj.AddOrUpdate<Jobs>("emails-fim-do-teste", j => j.EmailsFimDoTeste(), "0 13 * * *");
    }

    [AutomaticRetry(Attempts = 2)]
    public Task EnviarLembretes() => lembretes.EnviarLembretesAsync();

    /// <summary>Mantém sempre 8 semanas de sessões recorrentes geradas à frente.</summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task GerarRecorrencias()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        var recs = await db.Recorrencias.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.FimEm == null || r.FimEm >= hoje).ToListAsync();
        var tenants = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id);

        foreach (var rec in recs)
        {
            var t = tenants[rec.TenantId];
            var ate = Fuso.Hoje(Fuso.Obter(t.FusoHorario)).AddDays(7 * AgendaService.SemanasGeradasAFrente);
            if (rec.GeradoAte >= ate) continue;
            var (criadas, puladas) = await GeradorRecorrencias.GerarAsync(db, t, rec, ate);
            if (criadas + puladas > 0)
                log.LogInformation("Recorrência {Id}: {Criadas} sessões criadas, {Puladas} puladas por conflito", rec.Id, criadas, puladas);
        }
    }

    /// <summary>Nutrição do teste grátis: aviso 2 dias antes e no dia em que acaba (principal alavanca de conversão).</summary>
    public async Task EmailsFimDoTeste()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var agora = DateTime.UtcNow;
        var urlBase = cfg["Alento:UrlBase"]?.TrimEnd('/') ?? "";

        var candidatos = await db.Tenants.AsNoTracking()
            .Where(t => t.StatusAssinatura == StatusAssinatura.Teste
                        && t.TesteTerminaEm > agora.AddDays(-1) && t.TesteTerminaEm <= agora.AddDays(2))
            .ToListAsync();

        foreach (var t in candidatos)
        {
            var horas = (t.TesteTerminaEm - agora).TotalHours;
            var usuarios = await db.Users.AsNoTracking().Where(u => u.TenantId == t.Id).ToListAsync();
            var sessoes = await db.Agendamentos.IgnoreQueryFilters().CountAsync(a => a.TenantId == t.Id);
            var lembretes = await db.Mensagens.IgnoreQueryFilters().CountAsync(m => m.TenantId == t.Id);

            (string assunto, string corpo)? msg = horas switch
            {
                > 24 and <= 48 => ("Seu teste do Alento acaba em 2 dias",
                    $"Até agora você organizou <b>{sessoes} sessões</b> e o Alento enviou <b>{lembretes} lembretes</b> por você. " +
                    "Assine hoje para não perder nada: o primeiro pagamento só acontece quando o teste terminar."),
                <= 0 and > -24 => ("Seu teste terminou — sua agenda está esperando",
                    "Os lembretes automáticos foram pausados. Uma única falta evitada já paga o mês inteiro do Alento. " +
                    "Reative agora e continue de onde parou: seus pacientes e horários estão salvos."),
                _ => null
            };
            if (msg is null) continue;

            foreach (var u in usuarios.Where(u => u.Email is not null))
                await email.EnviarAsync(u.Email!, msg.Value.assunto,
                    ModeloEmail.Envolver(msg.Value.assunto, msg.Value.corpo, "Assinar por R$ 79/mês", $"{urlBase}/app/assinatura"));
        }
    }
}
