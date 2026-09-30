using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Alento.Web.Data;

public static class AlentoClaims
{
    public const string TenantId = "alento:tenant";
    public const string Nome = "alento:nome";
}

/// <summary>
/// Diz qual psicólogo (Tenant) está ativo neste escopo. Resolve na ordem:
/// valor definido explicitamente (jobs, página pública) → estado de autenticação do Blazor → HttpContext.
/// </summary>
public class TenantContext(IServiceProvider services)
{
    private Guid? _explicito;
    private Guid? _resolvido;

    public void Definir(Guid tenantId) => _explicito = tenantId;

    public Guid? TenantId => _explicito ?? (_resolvido ??= Resolver());

    public Guid TenantIdObrigatorio =>
        TenantId ?? throw new InvalidOperationException("Nenhum psicólogo ativo neste contexto.");

    public string NomeUsuario => Usuario()?.Identity?.Name ?? "sistema";

    private Guid? Resolver()
    {
        var claim = Usuario()?.FindFirst(AlentoClaims.TenantId)?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private ClaimsPrincipal? Usuario()
    {
        var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        try
        {
            var auth = services.GetService<AuthenticationStateProvider>();
            if (auth is not null)
            {
                var task = auth.GetAuthenticationStateAsync();
                if (task.IsCompletedSuccessfully && task.Result.User.Identity?.IsAuthenticated == true)
                    return task.Result.User;
            }
        }
        catch (InvalidOperationException)
        {
            // Fora de um circuito Blazor o provedor ainda não foi inicializado: cai para o HttpContext.
        }
        return http?.User;
    }
}
