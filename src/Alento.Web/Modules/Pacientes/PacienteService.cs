using System.Text.Json;
using Alento.Web.Data;
using Alento.Web.Modules.Auditoria;
using Alento.Web.Modules.Contas;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Pacientes;

public record ResumoPaciente(
    Guid Id, string Nome, string WhatsApp, string? Email, bool Ativo,
    int Sessoes, int Faltas, decimal EmAberto, DateTime? ProximaSessao);

public record HistoricoItem(Guid AgendamentoId, DateTime InicioUtc, string Servico, StatusAgendamento Status, decimal Valor, StatusCobranca? Cobranca, DateTime? PagoEm);

public record DetalhePaciente(Paciente Paciente, List<HistoricoItem> Historico)
{
    public int Realizadas => Historico.Count(h => h.Status == StatusAgendamento.Realizado);
    public int Faltas => Historico.Count(h => h.Status == StatusAgendamento.Faltou);
    public decimal TaxaFaltas => Realizadas + Faltas == 0 ? 0 : (decimal)Faltas / (Realizadas + Faltas);
    public decimal Pago => Historico.Where(h => h.Cobranca == StatusCobranca.Paga).Sum(h => h.Valor);
    public decimal EmAberto => Historico.Where(h => h.Cobranca == StatusCobranca.Pendente).Sum(h => h.Valor);
}

public class PacienteValidator : AbstractValidator<Paciente>
{
    public PacienteValidator()
    {
        RuleFor(x => x.Nome).NotEmpty().WithMessage("Informe o nome.").MaximumLength(120);
        RuleFor(x => x.WhatsApp).Must(Texto.WhatsAppValido).WithMessage("WhatsApp inválido. Use DDD + número.");
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage("E-mail inválido.");
        RuleFor(x => x.ValorSessaoPersonalizado).GreaterThanOrEqualTo(0).When(x => x.ValorSessaoPersonalizado.HasValue);
        RuleFor(x => x.Observacoes).MaximumLength(500);
    }
}

public class PacienteService(
    IDbContextFactory<AppDbContext> dbf,
    ContaService conta,
    AuditoriaService auditoria,
    IValidator<Paciente> validator)
{
    public async Task<List<ResumoPaciente>> ListarAsync(string? busca = null, bool incluirInativos = false)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var agora = DateTime.UtcNow;
        var q = db.Pacientes.AsNoTracking().Where(p => incluirInativos || p.Ativo);
        if (!string.IsNullOrWhiteSpace(busca))
        {
            var termo = $"%{busca.Trim()}%";
            var digitos = new string(busca.Where(char.IsDigit).ToArray());
            q = q.Where(p => EF.Functions.ILike(p.Nome, termo) || (digitos.Length >= 4 && p.WhatsApp.Contains(digitos)));
        }

        return await q.OrderBy(p => p.Nome)
            .Select(p => new ResumoPaciente(
                p.Id, p.Nome, p.WhatsApp, p.Email, p.Ativo,
                db.Agendamentos.Count(a => a.PacienteId == p.Id && a.Status == StatusAgendamento.Realizado),
                db.Agendamentos.Count(a => a.PacienteId == p.Id && a.Status == StatusAgendamento.Faltou),
                db.Cobrancas.Where(c => c.Agendamento!.PacienteId == p.Id && c.Status == StatusCobranca.Pendente).Sum(c => (decimal?)c.Valor) ?? 0,
                db.Agendamentos.Where(a => a.PacienteId == p.Id && a.Inicio > agora && a.Status != StatusAgendamento.Cancelado)
                    .OrderBy(a => a.Inicio).Select(a => (DateTime?)a.Inicio).FirstOrDefault()))
            .ToListAsync();
    }

    public async Task<List<Paciente>> OpcoesAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Pacientes.AsNoTracking().Where(p => p.Ativo).OrderBy(p => p.Nome).ToListAsync();
    }

    public async Task<DetalhePaciente?> DetalheAsync(Guid id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.Pacientes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return null;
        var hist = await db.Agendamentos.AsNoTracking().Where(a => a.PacienteId == id)
            .OrderByDescending(a => a.Inicio)
            .Select(a => new HistoricoItem(a.Id, a.Inicio, a.Servico!.Nome, a.Status, a.Valor,
                a.Cobranca != null ? a.Cobranca.Status : null, a.Cobranca != null ? a.Cobranca.PagoEm : null))
            .ToListAsync();
        await auditoria.RegistrarAsync("visualizou", nameof(Paciente), id);
        return new DetalhePaciente(p, hist);
    }

    public async Task<Guid> SalvarAsync(Paciente dados)
    {
        await conta.GarantirEscritaAsync();
        await validator.ValidateAndThrowAsync(dados);
        await using var db = await dbf.CreateDbContextAsync();
        var whatsapp = Texto.NormalizarWhatsApp(dados.WhatsApp);

        var p = await db.Pacientes.FirstOrDefaultAsync(x => x.Id == dados.Id);
        var novo = p is null;
        if (await db.Pacientes.AnyAsync(x => x.WhatsApp == whatsapp && x.Id != dados.Id))
            throw new RegraNegocioException("Já existe um paciente com esse WhatsApp.");

        p ??= db.Pacientes.Add(new Paciente()).Entity;
        if (dados.ConsentimentoMensagens && !p.ConsentimentoMensagens) p.ConsentimentoEm = DateTime.UtcNow;
        p.Nome = dados.Nome.Trim();
        p.WhatsApp = whatsapp;
        p.Email = string.IsNullOrWhiteSpace(dados.Email) ? null : dados.Email.Trim();
        p.ConsentimentoMensagens = dados.ConsentimentoMensagens;
        p.ValorSessaoPersonalizado = dados.ValorSessaoPersonalizado;
        p.Observacoes = dados.Observacoes?.Trim();
        p.Ativo = dados.Ativo;
        await db.SaveChangesAsync();
        await auditoria.RegistrarAsync(novo ? "criou" : "alterou", nameof(Paciente), p.Id);
        return p.Id;
    }

    /// <summary>Direito do titular (LGPD art. 18): todos os dados do paciente num JSON legível.</summary>
    public async Task<(string nomeArquivo, byte[] conteudo)> ExportarAsync(Guid id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.Pacientes.AsNoTracking().FirstAsync(x => x.Id == id);
        var sessoes = await db.Agendamentos.AsNoTracking().Where(a => a.PacienteId == id).OrderBy(a => a.Inicio)
            .Select(a => new
            {
                inicio = a.Inicio, fim = a.Fim, servico = a.Servico!.Nome, status = a.Status.ToString(), valor = a.Valor,
                cobranca = a.Cobranca == null ? null : new { status = a.Cobranca.Status.ToString(), pagoEm = a.Cobranca.PagoEm },
                mensagens = a.Mensagens.Select(m => new { m.Canal, m.Template, status = m.Status.ToString(), m.CriadoEm })
            }).ToListAsync();

        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            exportadoEm = DateTime.UtcNow,
            paciente = new { p.Nome, p.WhatsApp, p.Email, p.ConsentimentoMensagens, p.ConsentimentoEm, p.CriadoEm, p.Observacoes },
            sessoes
        }, new JsonSerializerOptions { WriteIndented = true });

        await auditoria.RegistrarAsync("exportou", nameof(Paciente), id);
        return ($"paciente-{Texto.Slug(p.Nome)}.json", json);
    }

    /// <summary>Exclusão definitiva a pedido do titular. Remove sessões, mensagens e cobranças do paciente.</summary>
    public async Task ExcluirAsync(Guid id)
    {
        await conta.GarantirEscritaAsync();
        await using var db = await dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var ags = db.Agendamentos.Where(a => a.PacienteId == id);
        await db.Mensagens.Where(m => ags.Select(a => (Guid?)a.Id).Contains(m.AgendamentoId)).ExecuteDeleteAsync();
        await db.Cobrancas.Where(c => ags.Select(a => a.Id).Contains(c.AgendamentoId)).ExecuteDeleteAsync();
        await ags.ExecuteDeleteAsync();
        await db.Recorrencias.Where(r => r.PacienteId == id).ExecuteDeleteAsync();
        await db.Pacientes.Where(p => p.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();
        await auditoria.RegistrarAsync("excluiu", nameof(Paciente), id);
    }
}
