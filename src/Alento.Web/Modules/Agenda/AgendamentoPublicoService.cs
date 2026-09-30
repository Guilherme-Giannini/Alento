using Alento.Web.Data;
using Alento.Web.Modules.Contas;
using Alento.Web.Modules.Email;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Agenda;

public record PaginaPublica(Tenant Tenant, List<Servico> Servicos, bool Ativa);

public record SessaoPublica(Agendamento Agendamento, Tenant Tenant, string NomePaciente, string Servico, Modalidade Modalidade);

/// <summary>Tudo que o paciente faz sem login: escolher horário, confirmar, remarcar e cancelar pelo token.</summary>
public class AgendamentoPublicoService(
    IDbContextFactory<AppDbContext> dbf,
    TenantContext tenantContext,
    IValidator<ReservaPublica> validator,
    INotificador notificador,
    ILogger<AgendamentoPublicoService> log)
{
    public async Task<PaginaPublica?> PaginaAsync(string slug)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug.ToLower());
        if (tenant is null) return null;
        tenantContext.Definir(tenant.Id);

        var servicos = await db.Servicos.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenant.Id && s.Ativo && s.VisivelNoLinkPublico)
            .OrderBy(s => s.Nome).ToListAsync();
        return new PaginaPublica(tenant, servicos, RegrasAcesso.LinkPublicoAtivo(tenant, DateTime.UtcNow));
    }

    public async Task<IReadOnlyList<HorarioLivre>> HorariosAsync(Tenant tenant, Guid servicoId, Guid? ignorarAgendamentoId = null)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var servico = await db.Servicos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == servicoId && s.TenantId == tenant.Id && s.Ativo);
        if (servico is null) return [];
        var hoje = Fuso.Hoje(Fuso.Obter(tenant.FusoHorario));
        return await ConfiguracaoAgendaService.HorariosLivresAsync(
            db, tenant, servico, hoje, hoje.AddDays(tenant.JanelaAgendamentoDias), aplicarAntecedencia: true, ignorarAgendamentoId);
    }

    public async Task<string> ReservarAsync(Tenant tenant, ReservaPublica r)
    {
        if (!RegrasAcesso.LinkPublicoAtivo(tenant, DateTime.UtcNow))
            throw new RegraNegocioException("A agenda deste profissional não está recebendo agendamentos no momento.");
        await validator.ValidateAndThrowAsync(r);

        // O horário precisa estar entre os livres agora (antecedência, janela, bloqueios), não só no momento em que a página abriu.
        var livres = await HorariosAsync(tenant, r.ServicoId);
        if (!livres.Any(h => h.InicioUtc == r.InicioUtc)) throw new HorarioIndisponivelException();

        await using var db = await dbf.CreateDbContextAsync();
        tenantContext.Definir(tenant.Id);
        var servico = await db.Servicos.FirstAsync(s => s.Id == r.ServicoId);
        var whatsapp = Texto.NormalizarWhatsApp(r.WhatsApp);

        var paciente = await db.Pacientes.FirstOrDefaultAsync(p => p.WhatsApp == whatsapp);
        if (paciente is null)
        {
            paciente = new Paciente { Nome = r.Nome.Trim(), WhatsApp = whatsapp, Email = r.Email?.Trim() };
            db.Pacientes.Add(paciente);
        }
        paciente.Ativo = true;
        paciente.ConsentimentoMensagens = true;
        paciente.ConsentimentoEm = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(paciente.Email) && !string.IsNullOrWhiteSpace(r.Email)) paciente.Email = r.Email.Trim();

        var ag = new Agendamento
        {
            PacienteId = paciente.Id,
            ServicoId = servico.Id,
            Inicio = r.InicioUtc,
            Fim = r.InicioUtc.AddMinutes(servico.DuracaoMinutos),
            Valor = paciente.ValorSessaoPersonalizado ?? servico.Valor,
            CriadoPeloPaciente = true
        };
        db.Agendamentos.Add(ag);
        await ConflitoAgenda.SalvarAsync(db);

        await NotificarPsicologoAsync(tenant, $"Nova sessão agendada por {paciente.Nome}", ag.Inicio);
        return ag.TokenPublico;
    }

    public async Task<SessaoPublica?> SessaoAsync(string token)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var ag = await db.Agendamentos.IgnoreQueryFilters().AsNoTracking()
            .Include(a => a.Paciente).Include(a => a.Servico)
            .FirstOrDefaultAsync(a => a.TokenPublico == token);
        if (ag is null) return null;
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == ag.TenantId);
        tenantContext.Definir(tenant.Id);
        return new SessaoPublica(ag, tenant, ag.Paciente!.Nome, ag.Servico!.Nome, ag.Servico.Modalidade);
    }

    public async Task ConfirmarAsync(string token)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var ag = await PorTokenAsync(db, token);
        if (ag.Status == StatusAgendamento.Agendado)
        {
            ag.Status = StatusAgendamento.Confirmado;
            ag.ConfirmadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task CancelarAsync(string token)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var ag = await PorTokenAsync(db, token);
        if (ag.Status is StatusAgendamento.Agendado or StatusAgendamento.Confirmado)
        {
            ag.Status = StatusAgendamento.Cancelado;
            await db.SaveChangesAsync();
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == ag.TenantId);
            await NotificarPsicologoAsync(tenant, "Um paciente cancelou a sessão", ag.Inicio);
        }
    }

    public async Task RemarcarAsync(string token, DateTime novoInicioUtc)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var ag = await PorTokenAsync(db, token);
        if (ag.Status is not (StatusAgendamento.Agendado or StatusAgendamento.Confirmado))
            throw new RegraNegocioException("Essa sessão não pode mais ser remarcada por aqui. Fale com seu psicólogo.");

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == ag.TenantId);
        var livres = await HorariosAsync(tenant, ag.ServicoId, ag.Id);
        if (!livres.Any(h => h.InicioUtc == novoInicioUtc)) throw new HorarioIndisponivelException();

        var duracao = ag.Fim - ag.Inicio;
        ag.Inicio = novoInicioUtc;
        ag.Fim = novoInicioUtc + duracao;
        ag.Status = StatusAgendamento.Confirmado;
        ag.ConfirmadoEm = DateTime.UtcNow;
        await ConflitoAgenda.SalvarAsync(db);
        await NotificarPsicologoAsync(tenant, "Um paciente remarcou a sessão", ag.Inicio);
    }

    private async Task<Agendamento> PorTokenAsync(AppDbContext db, string token)
    {
        var ag = await db.Agendamentos.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.TokenPublico == token)
                 ?? throw new RegraNegocioException("Sessão não encontrada.");
        tenantContext.Definir(ag.TenantId);
        return ag;
    }

    private async Task NotificarPsicologoAsync(Tenant tenant, string assunto, DateTime inicioUtc)
    {
        try
        {
            var local = Fuso.ParaLocal(inicioUtc, Fuso.Obter(tenant.FusoHorario));
            await notificador.NotificarPsicologoAsync(tenant.Id, assunto,
                $"{assunto}: {local.ToString("dddd, dd/MM 'às' HH:mm", Fuso.PtBr)}. Veja na sua agenda do Alento.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha ao notificar o psicólogo {Tenant}", tenant.Id);
        }
    }
}
