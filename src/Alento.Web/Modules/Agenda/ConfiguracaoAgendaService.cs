using Alento.Web.Data;
using Alento.Web.Modules.Contas;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Agenda;

/// <summary>Serviços oferecidos, horários de atendimento e bloqueios (férias, feriados).</summary>
public class ConfiguracaoAgendaService(
    IDbContextFactory<AppDbContext> dbf,
    ContaService conta,
    IValidator<Servico> servicoValidator,
    IValidator<Disponibilidade> dispValidator,
    IValidator<Bloqueio> bloqueioValidator)
{
    public async Task<List<Servico>> ServicosAsync(bool somenteAtivos = false)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Servicos.AsNoTracking()
            .Where(s => !somenteAtivos || s.Ativo)
            .OrderBy(s => s.Nome).ToListAsync();
    }

    public async Task SalvarServicoAsync(Servico s)
    {
        await conta.GarantirEscritaAsync();
        await servicoValidator.ValidateAndThrowAsync(s);
        await using var db = await dbf.CreateDbContextAsync();
        var existente = await db.Servicos.FirstOrDefaultAsync(x => x.Id == s.Id);
        if (existente is null)
        {
            db.Servicos.Add(new Servico
            {
                Nome = s.Nome.Trim(), DuracaoMinutos = s.DuracaoMinutos, Valor = s.Valor,
                Modalidade = s.Modalidade, Ativo = s.Ativo, VisivelNoLinkPublico = s.VisivelNoLinkPublico
            });
        }
        else
        {
            existente.Nome = s.Nome.Trim();
            existente.DuracaoMinutos = s.DuracaoMinutos;
            existente.Valor = s.Valor;
            existente.Modalidade = s.Modalidade;
            existente.Ativo = s.Ativo;
            existente.VisivelNoLinkPublico = s.VisivelNoLinkPublico;
        }
        await db.SaveChangesAsync();
    }

    public async Task<List<Disponibilidade>> DisponibilidadesAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var lista = await db.Disponibilidades.AsNoTracking().ToListAsync();
        // Semana começando na segunda-feira, como o psicólogo pensa a agenda.
        return lista.OrderBy(d => ((int)d.DiaDaSemana + 6) % 7).ThenBy(d => d.Inicio).ToList();
    }

    public async Task AdicionarDisponibilidadeAsync(Disponibilidade d)
    {
        await conta.GarantirEscritaAsync();
        await dispValidator.ValidateAndThrowAsync(d);
        await using var db = await dbf.CreateDbContextAsync();
        var mesmoDia = await db.Disponibilidades.Where(x => x.DiaDaSemana == d.DiaDaSemana).ToListAsync();
        if (mesmoDia.Any(x => x.Inicio < d.Fim && d.Inicio < x.Fim))
            throw new RegraNegocioException("Esse intervalo se sobrepõe a outro horário do mesmo dia.");
        db.Disponibilidades.Add(new Disponibilidade { DiaDaSemana = d.DiaDaSemana, Inicio = d.Inicio, Fim = d.Fim });
        await db.SaveChangesAsync();
    }

    public async Task RemoverDisponibilidadeAsync(Guid id)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        await db.Disponibilidades.Where(x => x.Id == id).ExecuteDeleteAsync();
    }

    public async Task<List<Bloqueio>> BloqueiosFuturosAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var agora = DateTime.UtcNow;
        return await db.Bloqueios.AsNoTracking().Where(b => b.Fim > agora).OrderBy(b => b.Inicio).ToListAsync();
    }

    public async Task AdicionarBloqueioAsync(Bloqueio b)
    {
        await conta.GarantirEscritaAsync();
        await bloqueioValidator.ValidateAndThrowAsync(b);
        await using var db = await dbf.CreateDbContextAsync();
        db.Bloqueios.Add(new Bloqueio { Inicio = b.Inicio, Fim = b.Fim, Motivo = b.Motivo.Trim() });
        await db.SaveChangesAsync();
    }

    public async Task RemoverBloqueioAsync(Guid id)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        await db.Bloqueios.Where(x => x.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Monta os parâmetros da calculadora com os dados do banco (usado no painel e na página pública).</summary>
    internal static async Task<IReadOnlyList<HorarioLivre>> HorariosLivresAsync(
        AppDbContext db, Tenant tenant, Servico servico, DateOnly de, DateOnly ate, bool aplicarAntecedencia, Guid? ignorarAgendamentoId = null)
    {
        var tz = Fuso.Obter(tenant.FusoHorario);
        var inicioUtc = Fuso.ParaUtc(de, TimeOnly.MinValue, tz).AddDays(-1);
        var fimUtc = Fuso.ParaUtc(ate.AddDays(1), TimeOnly.MinValue, tz).AddDays(1);

        var disp = await db.Disponibilidades.AsNoTracking().Where(d => d.TenantId == tenant.Id).ToListAsync();
        var bloqueios = await db.Bloqueios.AsNoTracking()
            .Where(b => b.TenantId == tenant.Id && b.Inicio < fimUtc && b.Fim > inicioUtc)
            .Select(b => new Intervalo(b.Inicio, b.Fim)).ToListAsync();
        var ocupados = await db.Agendamentos.AsNoTracking()
            .Where(a => a.TenantId == tenant.Id && a.Status != StatusAgendamento.Cancelado
                        && a.Inicio < fimUtc && a.Fim > inicioUtc && a.Id != ignorarAgendamentoId)
            .Select(a => new Intervalo(a.Inicio, a.Fim)).ToListAsync();

        return CalculadoraHorarios.Calcular(new ParametrosHorarios
        {
            Fuso = tz,
            Disponibilidade = disp.Select(d => new JanelaSemanal(d.DiaDaSemana, d.Inicio, d.Fim)).ToList(),
            Bloqueios = bloqueios,
            Ocupados = ocupados,
            DuracaoMinutos = servico.DuracaoMinutos,
            IntervaloMinutos = tenant.IntervaloEntreSessoesMinutos,
            De = de,
            Ate = ate,
            AgoraUtc = DateTime.UtcNow,
            AntecedenciaMinima = aplicarAntecedencia ? TimeSpan.FromHours(tenant.AntecedenciaMinimaHoras) : TimeSpan.Zero
        });
    }
}
