using Alento.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Contas;

public class RegraNegocioException(string mensagem) : Exception(mensagem);

public enum NivelAcesso
{
    Total,
    SomenteLeitura,
    Bloqueado
}

public static class RegrasAcesso
{
    public const int DiasDeTeste = 7;

    /// <summary>
    /// Teste vencido ou inadimplente bloqueia (só a tela de assinatura e a exportação ficam abertas).
    /// Cancelada vira somente leitura: a Resolução CFP 001/2009 exige guardar o registro por 5 anos.
    /// </summary>
    public static NivelAcesso Calcular(Tenant t, DateTime agoraUtc) => t.StatusAssinatura switch
    {
        StatusAssinatura.Ativa => NivelAcesso.Total,
        StatusAssinatura.Teste when t.TesteTerminaEm > agoraUtc => NivelAcesso.Total,
        StatusAssinatura.Cancelada => NivelAcesso.SomenteLeitura,
        _ => NivelAcesso.Bloqueado
    };

    public static int DiasRestantesTeste(Tenant t, DateTime agoraUtc) =>
        t.StatusAssinatura == StatusAssinatura.Teste
            ? Math.Max(0, (int)Math.Ceiling((t.TesteTerminaEm - agoraUtc).TotalDays))
            : 0;

    public static bool LinkPublicoAtivo(Tenant t, DateTime agoraUtc) => Calcular(t, agoraUtc) == NivelAcesso.Total;
}

public class ContaService(IDbContextFactory<AppDbContext> dbf, TenantContext tenant)
{
    public async Task<Tenant> AtualAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenant.TenantIdObrigatorio);
    }

    public async Task<NivelAcesso> NivelAsync() => RegrasAcesso.Calcular(await AtualAsync(), DateTime.UtcNow);

    public async Task GarantirEscritaAsync()
    {
        var nivel = await NivelAsync();
        if (nivel != NivelAcesso.Total)
            throw new RegraNegocioException(nivel == NivelAcesso.SomenteLeitura
                ? "Sua assinatura foi cancelada: a conta está em modo somente leitura. Reative para voltar a agendar."
                : "Seu acesso está suspenso. Ative a assinatura para continuar.");
    }

    public async Task<bool> SlugDisponivelAsync(string slug)
    {
        slug = Texto.Slug(slug);
        if (SlugsReservados.Contem(slug) || slug.Length < 3) return false;
        await using var db = await dbf.CreateDbContextAsync();
        return !await db.Tenants.AnyAsync(t => t.Slug == slug && t.Id != tenant.TenantIdObrigatorio);
    }

    public async Task SalvarPerfilAsync(Tenant dados)
    {
        await GarantirEscritaAsync();
        var slug = Texto.Slug(dados.Slug);
        if (!await SlugDisponivelAsync(slug))
            throw new RegraNegocioException("Esse endereço de link já está em uso ou é reservado. Tente outro.");

        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.Tenants.FirstAsync(x => x.Id == tenant.TenantIdObrigatorio);
        t.Nome = dados.Nome.Trim();
        t.Slug = slug;
        t.Crp = dados.Crp?.Trim();
        t.FusoHorario = Fuso.Obter(dados.FusoHorario).Id;
        t.ChavePix = dados.ChavePix?.Trim();
        t.CidadePix = dados.CidadePix?.Trim();
        t.WhatsApp = string.IsNullOrWhiteSpace(dados.WhatsApp) ? null : Texto.NormalizarWhatsApp(dados.WhatsApp);
        t.Bio = dados.Bio?.Trim();
        t.IntervaloEntreSessoesMinutos = Math.Clamp(dados.IntervaloEntreSessoesMinutos, 0, 120);
        t.AntecedenciaMinimaHoras = Math.Clamp(dados.AntecedenciaMinimaHoras, 0, 168);
        t.JanelaAgendamentoDias = Math.Clamp(dados.JanelaAgendamentoDias, 7, 90);
        await db.SaveChangesAsync();
    }
}

public static class SlugsReservados
{
    private static readonly HashSet<string> Lista =
    [
        "app", "account", "api", "admin", "hangfire", "webhooks", "s", "sessao", "entrar", "cadastro", "login",
        "precos", "planos", "termos", "privacidade", "dpa", "ajuda", "suporte", "blog", "not-found", "error",
        "lib", "css", "js", "img", "_framework", "_content", "favicon", "robots", "sitemap", "health"
    ];

    public static bool Contem(string slug) => Lista.Contains(slug);
}
