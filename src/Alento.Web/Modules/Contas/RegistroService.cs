using Alento.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Contas;

/// <summary>Cria o psicólogo (Tenant) com uma configuração inicial pronta para usar em 2 minutos.</summary>
public class RegistroService(IDbContextFactory<AppDbContext> dbf, TenantContext tenant)
{
    public async Task<Tenant> CriarTenantAsync(string nome, string? crp, string? whatsApp, string? origem = null)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var baseSlug = Texto.Slug(nome);
        if (baseSlug.Length < 3) baseSlug = "psi-" + baseSlug;
        var slug = baseSlug;
        for (var i = 2; SlugsReservados.Contem(slug) || await db.Tenants.AnyAsync(t => t.Slug == slug); i++)
            slug = $"{baseSlug}-{i}";

        var agora = DateTime.UtcNow;
        var t = new Tenant
        {
            Nome = nome.Trim(),
            Slug = slug,
            Crp = crp?.Trim(),
            WhatsApp = string.IsNullOrWhiteSpace(whatsApp) ? null : Texto.NormalizarWhatsApp(whatsApp),
            StatusAssinatura = StatusAssinatura.Teste,
            TesteTerminaEm = agora.AddDays(RegrasAcesso.DiasDeTeste),
            DpaAceitoEm = agora,
            OrigemCadastro = string.IsNullOrWhiteSpace(origem) ? null : origem[..Math.Min(200, origem.Length)]
        };
        db.Tenants.Add(t);

        // Padrões sensatos: o psicólogo já sai com um link de agendamento funcionando.
        db.Servicos.Add(new Servico { TenantId = t.Id, Nome = "Sessão individual", DuracaoMinutos = 50, Valor = 150m });
        db.Servicos.Add(new Servico { TenantId = t.Id, Nome = "Sessão online", DuracaoMinutos = 50, Valor = 150m, Modalidade = Modalidade.Online });
        foreach (var dia in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
        {
            db.Disponibilidades.Add(new Disponibilidade { TenantId = t.Id, DiaDaSemana = dia, Inicio = new(8, 0), Fim = new(12, 0) });
            db.Disponibilidades.Add(new Disponibilidade { TenantId = t.Id, DiaDaSemana = dia, Inicio = new(14, 0), Fim = new(19, 0) });
        }
        db.Assinaturas.Add(new Assinatura { TenantId = t.Id, Status = StatusAssinatura.Teste, Valor = PlanoAlento.ValorMensal });

        tenant.Definir(t.Id);
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>Desfaz um Tenant recém-criado quando a criação do usuário falha (e-mail duplicado em corrida, etc.).</summary>
    public async Task DescartarTenantAsync(Guid tenantId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await db.Assinaturas.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Disponibilidades.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Servicos.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
        await db.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
    }
}

public static class PlanoAlento
{
    public const decimal ValorMensal = 79m;
    public const string Nome = "Alento Profissional";
}
