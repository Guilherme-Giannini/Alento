using Alento.Web.Data;
using Alento.Web.Modules.Contas;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Agenda;

public record ItemAgenda(
    Guid Id,
    Guid PacienteId,
    string Paciente,
    string WhatsApp,
    string Servico,
    Modalidade Modalidade,
    DateTime InicioUtc,
    DateTime FimUtc,
    StatusAgendamento Status,
    string? LinkVideo,
    decimal Valor,
    bool Recorrente,
    StatusMensagem? StatusLembrete,
    StatusCobranca? StatusCobranca,
    Guid? CobrancaId,
    bool CriadoPeloPaciente);

public class AgendaService(
    IDbContextFactory<AppDbContext> dbf,
    ContaService conta,
    IValidator<NovoAgendamento> novoValidator,
    IValidator<NovaRecorrencia> recValidator)
{
    public const int SemanasGeradasAFrente = 8;

    public async Task<List<ItemAgenda>> ListarAsync(DateTime deUtc, DateTime ateUtc, bool incluirCancelados = false)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Agendamentos.AsNoTracking()
            .Where(a => a.Inicio < ateUtc && a.Fim > deUtc && (incluirCancelados || a.Status != StatusAgendamento.Cancelado))
            .OrderBy(a => a.Inicio)
            .Select(a => new ItemAgenda(
                a.Id, a.PacienteId, a.Paciente!.Nome, a.Paciente.WhatsApp, a.Servico!.Nome, a.Servico.Modalidade,
                a.Inicio, a.Fim, a.Status, a.LinkVideo, a.Valor, a.RecorrenciaId != null,
                a.Mensagens.OrderByDescending(m => m.CriadoEm).Select(m => (StatusMensagem?)m.Status).FirstOrDefault(),
                a.Cobranca != null ? a.Cobranca.Status : null,
                a.Cobranca != null ? a.Cobranca.Id : null,
                a.CriadoPeloPaciente))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<HorarioLivre>> HorariosLivresAsync(Guid servicoId, DateOnly de, DateOnly ate, Guid? ignorar = null)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == db.TenantAtual);
        var servico = await db.Servicos.AsNoTracking().FirstAsync(s => s.Id == servicoId);
        return await ConfiguracaoAgendaService.HorariosLivresAsync(db, tenant, servico, de, ate, aplicarAntecedencia: false, ignorar);
    }

    public async Task<Guid> CriarAsync(NovoAgendamento input)
    {
        await conta.GarantirEscritaAsync();
        await novoValidator.ValidateAndThrowAsync(input);
        await using var db = await dbf.CreateDbContextAsync();

        var servico = await db.Servicos.AsNoTracking().FirstOrDefaultAsync(s => s.Id == input.ServicoId)
                      ?? throw new RegraNegocioException("Serviço não encontrado.");
        var paciente = await db.Pacientes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == input.PacienteId)
                       ?? throw new RegraNegocioException("Paciente não encontrado.");

        var fim = input.InicioUtc.AddMinutes(servico.DuracaoMinutos);
        await GarantirSemBloqueioAsync(db, input.InicioUtc, fim);

        var ag = new Agendamento
        {
            PacienteId = paciente.Id,
            ServicoId = servico.Id,
            Inicio = input.InicioUtc,
            Fim = fim,
            LinkVideo = string.IsNullOrWhiteSpace(input.LinkVideo) ? null : input.LinkVideo.Trim(),
            Valor = input.Valor ?? paciente.ValorSessaoPersonalizado ?? servico.Valor
        };
        db.Agendamentos.Add(ag);
        await ConflitoAgenda.SalvarAsync(db);
        return ag.Id;
    }

    public async Task RemarcarAsync(Guid id, DateTime novoInicioUtc)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        var ag = await db.Agendamentos.FirstAsync(a => a.Id == id);
        var duracao = ag.Fim - ag.Inicio;
        await GarantirSemBloqueioAsync(db, novoInicioUtc, novoInicioUtc + duracao);
        ag.Inicio = novoInicioUtc;
        ag.Fim = novoInicioUtc + duracao;
        ag.Status = StatusAgendamento.Agendado;
        ag.ConfirmadoEm = null;
        await ConflitoAgenda.SalvarAsync(db);
    }

    public async Task AtualizarLinkVideoAsync(Guid id, string? link)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        var ag = await db.Agendamentos.FirstAsync(a => a.Id == id);
        ag.LinkVideo = string.IsNullOrWhiteSpace(link) ? null : link.Trim();
        await db.SaveChangesAsync();
    }

    /// <summary>Muda o status. Sessão realizada gera a cobrança automaticamente (se tiver valor).</summary>
    public async Task AlterarStatusAsync(Guid id, StatusAgendamento status, bool cobrarFalta = false)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        var ag = await db.Agendamentos.Include(a => a.Cobranca).FirstAsync(a => a.Id == id);
        ag.Status = status;
        if (status == StatusAgendamento.Confirmado) ag.ConfirmadoEm ??= DateTime.UtcNow;

        var gerarCobranca = status == StatusAgendamento.Realizado || status == StatusAgendamento.Faltou && cobrarFalta;
        if (gerarCobranca && ag.Cobranca is null && ag.Valor > 0)
            db.Cobrancas.Add(new Cobranca { AgendamentoId = ag.Id, Valor = ag.Valor });
        if (status == StatusAgendamento.Cancelado && ag.Cobranca is { Status: StatusCobranca.Pendente })
            ag.Cobranca.Status = StatusCobranca.Cancelada;

        await ConflitoAgenda.SalvarAsync(db);
    }

    public async Task<(int criadas, int puladas)> CriarRecorrenciaAsync(NovaRecorrencia input)
    {
        await conta.GarantirEscritaAsync();
        await recValidator.ValidateAndThrowAsync(input);
        await using var db = await dbf.CreateDbContextAsync();

        var rec = new Recorrencia
        {
            PacienteId = input.PacienteId,
            ServicoId = input.ServicoId,
            DiaDaSemana = input.DiaDaSemana,
            Horario = input.Horario,
            InicioEm = input.InicioEm,
            FimEm = input.FimEm,
            LinkVideo = string.IsNullOrWhiteSpace(input.LinkVideo) ? null : input.LinkVideo.Trim()
        };
        db.Recorrencias.Add(rec);
        await db.SaveChangesAsync();

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == db.TenantAtual);
        var ate = Fuso.Hoje(Fuso.Obter(tenant.FusoHorario)).AddDays(7 * SemanasGeradasAFrente);
        return await GeradorRecorrencias.GerarAsync(db, tenant, rec, ate);
    }

    public async Task<List<Recorrencia>> RecorrenciasAtivasAsync(Guid? pacienteId = null)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        return await db.Recorrencias.AsNoTracking().Include(r => r.Paciente).Include(r => r.Servico)
            .Where(r => (r.FimEm == null || r.FimEm >= hoje) && (pacienteId == null || r.PacienteId == pacienteId))
            .OrderBy(r => r.DiaDaSemana).ThenBy(r => r.Horario).ToListAsync();
    }

    /// <summary>Encerra a recorrência e cancela as sessões futuras geradas por ela.</summary>
    public async Task EncerrarRecorrenciaAsync(Guid recorrenciaId)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        var rec = await db.Recorrencias.FirstAsync(r => r.Id == recorrenciaId);
        var agora = DateTime.UtcNow;
        rec.FimEm = DateOnly.FromDateTime(agora);
        await db.SaveChangesAsync();
        await db.Agendamentos
            .Where(a => a.RecorrenciaId == rec.Id && a.Inicio > agora
                        && (a.Status == StatusAgendamento.Agendado || a.Status == StatusAgendamento.Confirmado))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, StatusAgendamento.Cancelado));
    }

    private static async Task GarantirSemBloqueioAsync(AppDbContext db, DateTime inicio, DateTime fim)
    {
        var bloqueio = await db.Bloqueios.AsNoTracking().FirstOrDefaultAsync(b => b.Inicio < fim && b.Fim > inicio);
        if (bloqueio is not null)
            throw new RegraNegocioException($"Esse horário cai num bloqueio da agenda{(string.IsNullOrWhiteSpace(bloqueio.Motivo) ? "" : $" ({bloqueio.Motivo})")}.");
    }
}

public static class GeradorRecorrencias
{
    /// <summary>
    /// Cria as sessões semanais até a data informada. Semanas com conflito (bloqueio ou horário ocupado)
    /// são puladas em vez de derrubar a série inteira.
    /// </summary>
    public static async Task<(int criadas, int puladas)> GerarAsync(AppDbContext db, Tenant tenant, Recorrencia rec, DateOnly ate)
    {
        var tz = Fuso.Obter(tenant.FusoHorario);
        var servico = await db.Servicos.IgnoreQueryFilters().AsNoTracking().FirstAsync(s => s.Id == rec.ServicoId);
        var paciente = await db.Pacientes.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == rec.PacienteId);
        var hoje = Fuso.Hoje(tz);

        var inicio = rec.GeradoAte?.AddDays(1) ?? rec.InicioEm;
        if (inicio < hoje) inicio = hoje;
        var limite = rec.FimEm is { } fimRec && fimRec < ate ? fimRec : ate;

        var dia = inicio;
        while (dia.DayOfWeek != rec.DiaDaSemana) dia = dia.AddDays(1);

        int criadas = 0, puladas = 0;
        var agora = DateTime.UtcNow;
        for (; dia <= limite; dia = dia.AddDays(7))
        {
            var ini = Fuso.ParaUtc(dia, rec.Horario, tz);
            var fim = ini.AddMinutes(servico.DuracaoMinutos);
            if (ini <= agora) continue;

            var conflito = await db.Agendamentos.IgnoreQueryFilters().AnyAsync(a => a.TenantId == tenant.Id
                               && a.Status != StatusAgendamento.Cancelado && a.Inicio < fim && a.Fim > ini)
                           || await db.Bloqueios.IgnoreQueryFilters().AnyAsync(b => b.TenantId == tenant.Id && b.Inicio < fim && b.Fim > ini);
            if (conflito) { puladas++; continue; }

            db.Agendamentos.Add(new Agendamento
            {
                TenantId = tenant.Id,
                PacienteId = rec.PacienteId,
                ServicoId = rec.ServicoId,
                RecorrenciaId = rec.Id,
                Inicio = ini,
                Fim = fim,
                LinkVideo = rec.LinkVideo,
                Valor = paciente.ValorSessaoPersonalizado ?? servico.Valor
            });
            try
            {
                await db.SaveChangesAsync();
                criadas++;
            }
            catch (DbUpdateException ex) when (ConflitoAgenda.EhSobreposicao(ex))
            {
                foreach (var e in db.ChangeTracker.Entries<Agendamento>().Where(e => e.State == EntityState.Added).ToList())
                    e.State = EntityState.Detached;
                puladas++;
            }
        }

        var r = await db.Recorrencias.IgnoreQueryFilters().FirstAsync(x => x.Id == rec.Id);
        r.GeradoAte = limite;
        await db.SaveChangesAsync();
        return (criadas, puladas);
    }
}
