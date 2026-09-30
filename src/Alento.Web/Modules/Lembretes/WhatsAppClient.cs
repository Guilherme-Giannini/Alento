using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Alento.Web.Modules.Lembretes;

public class WhatsAppOptions
{
    public string? Token { get; set; }
    public string? PhoneNumberId { get; set; }
    public string ApiVersion { get; set; } = "v23.0";
    public string? AppSecret { get; set; }
    public string? VerifyToken { get; set; }
    public string TemplateLembrete { get; set; } = "lembrete_sessao";
    public string Idioma { get; set; } = "pt_BR";
    public decimal CustoPorMensagem { get; set; } = 0.035m;

    public bool Configurado => !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(PhoneNumberId);
}

public record ResultadoEnvio(bool Sucesso, string? IdMeta, string? Erro);

public interface IWhatsAppClient
{
    /// <summary>Template aprovado com dois botões de resposta rápida (Confirmar / Remarcar).</summary>
    Task<ResultadoEnvio> EnviarLembreteAsync(string para, string nomePaciente, string nomeProfissional, string quando, string payloadConfirmar, string payloadRemarcar);

    /// <summary>Texto livre: só é permitido dentro da janela de 24h aberta pela resposta do paciente.</summary>
    Task<ResultadoEnvio> EnviarTextoAsync(string para, string texto);
}

public class WhatsAppClient(HttpClient http, IOptions<WhatsAppOptions> opt, ILogger<WhatsAppClient> log) : IWhatsAppClient
{
    private WhatsAppOptions O => opt.Value;

    public Task<ResultadoEnvio> EnviarLembreteAsync(string para, string nomePaciente, string nomeProfissional, string quando, string payloadConfirmar, string payloadRemarcar)
    {
        // Mensagem mínima (LGPD): sem a palavra "terapia" e sem nenhum detalhe clínico.
        // Template sugerido: "Olá, {{1}}! Lembrete: sua sessão com {{2}} é {{3}}. Podemos confirmar?"
        var corpo = new
        {
            messaging_product = "whatsapp",
            to = para,
            type = "template",
            template = new
            {
                name = O.TemplateLembrete,
                language = new { code = O.Idioma },
                components = new object[]
                {
                    new
                    {
                        type = "body",
                        parameters = new object[]
                        {
                            new { type = "text", text = nomePaciente },
                            new { type = "text", text = nomeProfissional },
                            new { type = "text", text = quando }
                        }
                    },
                    new { type = "button", sub_type = "quick_reply", index = "0", parameters = new object[] { new { type = "payload", payload = payloadConfirmar } } },
                    new { type = "button", sub_type = "quick_reply", index = "1", parameters = new object[] { new { type = "payload", payload = payloadRemarcar } } }
                }
            }
        };
        return EnviarAsync(corpo, $"[Lembrete] {nomePaciente}: sessão com {nomeProfissional} {quando}");
    }

    public Task<ResultadoEnvio> EnviarTextoAsync(string para, string texto) =>
        EnviarAsync(new { messaging_product = "whatsapp", to = para, type = "text", text = new { body = texto, preview_url = true } }, texto);

    private async Task<ResultadoEnvio> EnviarAsync(object corpo, string resumo)
    {
        if (!O.Configurado)
        {
            log.LogInformation("[WhatsApp simulado] {Resumo}", resumo);
            return new ResultadoEnvio(true, "sim." + Guid.NewGuid().ToString("N")[..16], null);
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{O.ApiVersion}/{O.PhoneNumberId}/messages")
        {
            Content = JsonContent.Create(corpo)
        };
        req.Headers.Authorization = new("Bearer", O.Token);
        try
        {
            var resp = await http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                log.LogError("WhatsApp recusou a mensagem: {Status} {Corpo}", resp.StatusCode, json);
                return new ResultadoEnvio(false, null, Truncar(json));
            }
            var r = JsonSerializer.Deserialize<RespostaMeta>(json);
            return new ResultadoEnvio(true, r?.Messages?.FirstOrDefault()?.Id, null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Erro de rede ao enviar WhatsApp");
            return new ResultadoEnvio(false, null, Truncar(ex.Message));
        }
    }

    /// <summary>Valida o cabeçalho X-Hub-Signature-256 (HMAC-SHA256 do corpo com o App Secret).</summary>
    public static bool AssinaturaValida(string? appSecret, string corpo, string? cabecalho)
    {
        if (string.IsNullOrWhiteSpace(appSecret)) return true; // desenvolvimento
        if (string.IsNullOrWhiteSpace(cabecalho) || !cabecalho.StartsWith("sha256=")) return false;
        var esperado = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(corpo)));
        return Seguranca.CompararConstante(esperado, cabecalho.ToLowerInvariant());
    }

    private static string Truncar(string s) => s.Length > 480 ? s[..480] : s;

    private sealed record RespostaMeta([property: JsonPropertyName("messages")] List<IdMeta>? Messages);
    private sealed record IdMeta([property: JsonPropertyName("id")] string Id);
}
