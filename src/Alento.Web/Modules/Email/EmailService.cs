using System.Net;
using System.Net.Http.Json;
using Alento.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Alento.Web.Modules.Email;

public class EmailOptions
{
    public string? ResendApiKey { get; set; }
    public string Remetente { get; set; } = "Alento <nao-responda@alento.app.br>";
}

public interface IEmailService
{
    Task EnviarAsync(string para, string assunto, string html);
}

/// <summary>Resend via HTTP. Sem chave configurada (desenvolvimento), só registra no log.</summary>
public class ResendEmailService(HttpClient http, IOptions<EmailOptions> opt, ILogger<ResendEmailService> log) : IEmailService
{
    public async Task EnviarAsync(string para, string assunto, string html)
    {
        if (string.IsNullOrWhiteSpace(opt.Value.ResendApiKey))
        {
            log.LogInformation("[E-mail simulado] Para: {Para} | Assunto: {Assunto}\n{Html}", para, assunto, html);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new { from = opt.Value.Remetente, to = new[] { para }, subject = assunto, html })
        };
        req.Headers.Authorization = new("Bearer", opt.Value.ResendApiKey);
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            log.LogError("Resend recusou o e-mail para {Para}: {Status} {Corpo}", para, resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }
}

public static class ModeloEmail
{
    public static string Envolver(string titulo, string corpoHtml, string? textoBotao = null, string? link = null) => $"""
        <div style="font-family:Arial,Helvetica,sans-serif;background:#f6f5f2;padding:32px 12px">
          <div style="max-width:520px;margin:0 auto;background:#fff;border-radius:16px;padding:32px;border:1px solid #ebe8e1">
            <div style="font-size:20px;font-weight:700;color:#1f4d45;margin-bottom:20px">Alento</div>
            <h1 style="font-size:20px;color:#1b1b1b;margin:0 0 12px">{titulo}</h1>
            <div style="font-size:15px;line-height:1.6;color:#444">{corpoHtml}</div>
            {(link is null ? "" : $"""<p style="margin:28px 0 8px"><a href="{link}" style="background:#1f4d45;color:#fff;padding:12px 22px;border-radius:10px;text-decoration:none;font-weight:700;display:inline-block">{textoBotao}</a></p>""")}
          </div>
          <p style="text-align:center;font-size:12px;color:#999;margin-top:16px">Alento · agenda para psicólogos</p>
        </div>
        """;
}

/// <summary>E-mails do Identity (confirmação de cadastro e redefinição de senha).</summary>
public class IdentityEmailSender(IEmailService email) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string emailAddr, string confirmationLink) =>
        email.EnviarAsync(emailAddr, "Confirme seu e-mail no Alento",
            ModeloEmail.Envolver($"Bem-vindo(a), {WebUtility.HtmlEncode(Texto.PrimeiroNome(user.Nome))}!",
                "Falta só confirmar seu e-mail para proteger sua conta.", "Confirmar e-mail", confirmationLink));

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string emailAddr, string resetLink) =>
        email.EnviarAsync(emailAddr, "Redefinir sua senha do Alento",
            ModeloEmail.Envolver("Redefinir senha",
                "Recebemos um pedido para redefinir sua senha. Se não foi você, ignore este e-mail.", "Criar nova senha", resetLink));

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string emailAddr, string resetCode) =>
        email.EnviarAsync(emailAddr, "Código para redefinir sua senha",
            ModeloEmail.Envolver("Seu código", $"Use este código para redefinir a senha: <b>{resetCode}</b>"));
}

public interface INotificador
{
    Task NotificarPsicologoAsync(Guid tenantId, string assunto, string texto);
}

public class NotificadorEmail(IDbContextFactory<AppDbContext> dbf, IEmailService email, IConfiguration cfg) : INotificador
{
    public async Task NotificarPsicologoAsync(Guid tenantId, string assunto, string texto)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var emails = await db.Users.AsNoTracking().Where(u => u.TenantId == tenantId)
            .Select(u => u.Email).Where(e => e != null).Distinct().ToListAsync();
        var urlBase = cfg["Alento:UrlBase"]?.TrimEnd('/') ?? "";
        foreach (var e in emails)
            await email.EnviarAsync(e!, assunto, ModeloEmail.Envolver(WebUtility.HtmlEncode(assunto), WebUtility.HtmlEncode(texto), "Abrir agenda", $"{urlBase}/app/agenda"));
    }
}
