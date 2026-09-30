using Alento.Web.Data;
using Alento.Web.Modules.Contas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Alento.Web.Modules.Lembretes;

public class LembreteService(
    IDbContextFactory<AppDbContext> dbf,
    IWhatsAppClient whatsApp,
    IOptions<WhatsAppOptions> opt,
    IConfiguration cfg,
    ILogger<LembreteService> log)
{
    public const string TemplateLembrete = "lembrete_24h";
    private const string PrefixoConfirmar = "CONFIRMAR:";
    private const string PrefixoRemarcar = "REMARCAR:";

    private string UrlBase => cfg["Alento:UrlBase"]?.TrimEnd('/') ?? "https://alento.app.br";

    /// <summary>
    /// Job do Hangfire (a cada 15 min): sessões que começam entre 20h e 24h à frente e ainda não receberam lembrete.
    /// A janela maior que 15 min cobre reinícios do servidor; a checagem de mensagem existente evita duplicidade.
    /// </summary>
    public async Task<int> EnviarLembretesAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var agora = DateTime.UtcNow;
        var de = agora.AddHours(20);
        var ate = agora.AddHours(24);

        var pendentes = await db.Agendamentos.IgnoreQueryFilters()
            .Include(a => a.Paciente)
            .Where(a => a.Status == StatusAgendamento.Agendado && a.Inicio > de && a.Inicio <= ate
                        && a.Paciente!.ConsentimentoMensagens
                        && !a.Mensagens.Any(m => m.Template == TemplateLembrete))
            .ToListAsync();
        if (pendentes.Count == 0) return 0;

        var tenantIds = pendentes.Select(a => a.TenantId).Distinct().ToList();
        var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);

        var enviados = 0;
        foreach (var ag in pendentes)
        {
            var tenant = tenants[ag.TenantId];
            if (RegrasAcesso.Calcular(tenant, agora) != NivelAcesso.Total) continue;

            var r = await whatsApp.EnviarLembreteAsync(
                ag.Paciente!.WhatsApp,
                Texto.PrimeiroNome(ag.Paciente.Nome),
                tenant.Nome,
                DescreverQuando(ag.Inicio, tenant),
                PrefixoConfirmar + ag.TokenPublico,
                PrefixoRemarcar + ag.TokenPublico);

            db.Mensagens.Add(new Mensagem
            {
                TenantId = ag.TenantId,
                AgendamentoId = ag.Id,
                Template = TemplateLembrete,
                Status = r.Sucesso ? StatusMensagem.Enviada : StatusMensagem.Falhou,
                IdMeta = r.IdMeta,
                Erro = r.Erro,
                Custo = r.Sucesso ? opt.Value.CustoPorMensagem : 0
            });
            await db.SaveChangesAsync();
            if (r.Sucesso) enviados++;
        }

        log.LogInformation("Lembretes enviados: {Enviados} de {Total}", enviados, pendentes.Count);
        return enviados;
    }

    public static string DescreverQuando(DateTime inicioUtc, Tenant tenant)
    {
        var tz = Fuso.Obter(tenant.FusoHorario);
        var local = Fuso.ParaLocal(inicioUtc, tz);
        var hoje = Fuso.Hoje(tz);
        var dia = DateOnly.FromDateTime(local);
        var prefixo = dia == hoje ? "hoje" : dia == hoje.AddDays(1) ? "amanhã" : local.ToString("dddd", Fuso.PtBr);
        return $"{prefixo} ({local:dd/MM}) às {local:HH'h'mm}";
    }

    /// <summary>Paciente tocou num botão do lembrete.</summary>
    public async Task ProcessarRespostaAsync(string de, string payload)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var token = payload.StartsWith(PrefixoConfirmar) ? payload[PrefixoConfirmar.Length..]
                  : payload.StartsWith(PrefixoRemarcar) ? payload[PrefixoRemarcar.Length..]
                  : null;
        if (token is null) return;

        var ag = await db.Agendamentos.IgnoreQueryFilters().Include(a => a.Paciente)
            .FirstOrDefaultAsync(a => a.TokenPublico == token);
        // O número que respondeu precisa ser o do paciente da sessão.
        if (ag is null || Texto.NormalizarWhatsApp(de) != ag.Paciente!.WhatsApp) return;

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == ag.TenantId);
        string resposta;
        if (payload.StartsWith(PrefixoConfirmar))
        {
            if (ag.Status == StatusAgendamento.Agendado)
            {
                ag.Status = StatusAgendamento.Confirmado;
                ag.ConfirmadoEm = DateTime.UtcNow;
            }
            resposta = $"Sessão confirmada! ✅ Até {DescreverQuando(ag.Inicio, tenant)}." +
                       (string.IsNullOrWhiteSpace(ag.LinkVideo) ? "" : $"\nLink da chamada: {ag.LinkVideo}");
        }
        else
        {
            resposta = $"Sem problemas! Escolha um novo horário por aqui: {UrlBase}/s/{ag.TokenPublico}";
        }
        await db.SaveChangesAsync();

        var r = await whatsApp.EnviarTextoAsync(ag.Paciente.WhatsApp, resposta);
        db.Mensagens.Add(new Mensagem
        {
            TenantId = ag.TenantId,
            AgendamentoId = ag.Id,
            Template = payload.StartsWith(PrefixoConfirmar) ? "resposta_confirmado" : "resposta_remarcar",
            Status = r.Sucesso ? StatusMensagem.Enviada : StatusMensagem.Falhou,
            IdMeta = r.IdMeta,
            Erro = r.Erro
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Atualização de entrega vinda da Meta (sent, delivered, read, failed).</summary>
    public async Task AtualizarStatusAsync(string idMeta, string status, string? erro)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var m = await db.Mensagens.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.IdMeta == idMeta);
        if (m is null) return;
        var novo = status switch
        {
            "sent" => StatusMensagem.Enviada,
            "delivered" => StatusMensagem.Entregue,
            "read" => StatusMensagem.Lida,
            "failed" => StatusMensagem.Falhou,
            _ => m.Status
        };
        // Status só avança (a Meta pode entregar os eventos fora de ordem), exceto falha.
        if (novo == StatusMensagem.Falhou || novo > m.Status)
        {
            m.Status = novo;
            m.Erro = erro ?? m.Erro;
            m.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }
}
