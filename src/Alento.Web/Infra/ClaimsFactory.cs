using System.Security.Claims;
using Alento.Web.Data;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Alento.Web.Infra;

/// <summary>Coloca o TenantId no cookie de login: é daí que o filtro global do EF Core tira o psicólogo ativo.</summary>
public class AlentoClaimsFactory(UserManager<ApplicationUser> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        id.AddClaim(new Claim(AlentoClaims.TenantId, user.TenantId.ToString()));
        id.AddClaim(new Claim(AlentoClaims.Nome, user.Nome));
        return id;
    }
}

/// <summary>Painel do Hangfire só para os e-mails listados em Alento:Admins.</summary>
public class HangfireAdminFilter(string[] admins) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true
               && admins.Contains(user.Identity.Name, StringComparer.OrdinalIgnoreCase);
    }
}
