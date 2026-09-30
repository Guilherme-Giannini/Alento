using System.Text.Json;
using Alento.Web.Modules.Assinaturas;
using Alento.Web.Modules.Lembretes;
using Microsoft.Extensions.Options;

namespace Alento.Web.Modules.Webhooks;

public static class WebhookEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapWebhooks(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/webhooks").DisableAntiforgery().AllowAnonymous();

        // Verificação do webhook pela Meta (feita uma vez, ao cadastrar a URL).
        g.MapGet("/whatsapp", (HttpRequest req, IOptions<WhatsAppOptions> opt) =>
        {
            var mode = req.Query["hub.mode"].ToString();
            var token = req.Query["hub.verify_token"].ToString();
            var challenge = req.Query["hub.challenge"].ToString();
            return mode == "subscribe" && !string.IsNullOrEmpty(opt.Value.VerifyToken)
                   && Seguranca.CompararConstante(token, opt.Value.VerifyToken)
                ? Results.Text(challenge)
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        g.MapPost("/whatsapp", async (HttpRequest req, IOptions<WhatsAppOptions> opt, LembreteService lembretes, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("Webhooks.WhatsApp");
            using var reader = new StreamReader(req.Body);
            var corpo = await reader.ReadToEndAsync();
            if (!WhatsAppClient.AssinaturaValida(opt.Value.AppSecret, corpo, req.Headers["X-Hub-Signature-256"]))
            {
                log.LogWarning("Webhook do WhatsApp com assinatura inválida");
                return Results.Unauthorized();
            }

            try
            {
                using var doc = JsonDocument.Parse(corpo);
                foreach (var entry in Array(doc.RootElement, "entry"))
                foreach (var change in Array(entry, "changes"))
                {
                    if (!change.TryGetProperty("value", out var value)) continue;

                    foreach (var msg in Array(value, "messages"))
                    {
                        var de = Str(msg, "from");
                        // Resposta a botão de template chega como type=button; a interativa, como button_reply.
                        var payload = msg.TryGetProperty("button", out var b) ? Str(b, "payload")
                            : msg.TryGetProperty("interactive", out var i) && i.TryGetProperty("button_reply", out var br) ? Str(br, "id")
                            : null;
                        if (de is not null && payload is not null)
                            await lembretes.ProcessarRespostaAsync(de, payload);
                    }

                    foreach (var st in Array(value, "statuses"))
                    {
                        var id = Str(st, "id");
                        var status = Str(st, "status");
                        var erro = Array(st, "errors").Select(e => Str(e, "title")).FirstOrDefault();
                        if (id is not null && status is not null)
                            await lembretes.AtualizarStatusAsync(id, status, erro);
                    }
                }
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Webhook do WhatsApp com JSON inválido");
                return Results.BadRequest();
            }
            // A Meta reenvia se não receber 200 rápido.
            return Results.Ok();
        });

        g.MapPost("/asaas", async (HttpRequest req, IOptions<AsaasOptions> opt, AsaasWebhookProcessor proc, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("Webhooks.Asaas");
            var token = req.Headers["asaas-access-token"].ToString();
            if (string.IsNullOrEmpty(opt.Value.WebhookToken) || !Seguranca.CompararConstante(token, opt.Value.WebhookToken))
            {
                log.LogWarning("Webhook do Asaas com token inválido");
                return Results.Unauthorized();
            }

            var evento = await req.ReadFromJsonAsync<EventoAsaas>(Json);
            if (evento is null) return Results.BadRequest();
            await proc.ProcessarAsync(evento);
            return Results.Ok(new { received = true });
        });
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string nome) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
            : [];

    private static string? Str(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
