using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Alento.Web.Modules.Assinaturas;

public class AsaasOptions
{
    public string BaseUrl { get; set; } = "https://api-sandbox.asaas.com/v3";
    public string? ApiKey { get; set; }
    /// <summary>Token definido ao cadastrar o webhook no Asaas; chega no cabeçalho asaas-access-token.</summary>
    public string? WebhookToken { get; set; }

    public bool Configurado => !string.IsNullOrWhiteSpace(ApiKey);
}

public class AsaasException(string mensagem) : Exception(mensagem);

public class AsaasClient(HttpClient http, IOptions<AsaasOptions> opt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool Configurado => opt.Value.Configurado;

    public async Task<string> CriarClienteAsync(string nome, string email, string cpfCnpj, string? celular, string referencia)
    {
        var r = await EnviarAsync<IdResposta>(HttpMethod.Post, "customers",
            new { name = nome, email, cpfCnpj, mobilePhone = celular, externalReference = referencia, notificationDisabled = false });
        return r.Id;
    }

    public async Task<string> CriarAssinaturaAsync(string clienteId, decimal valor, DateOnly primeiroVencimento, string descricao, string referencia)
    {
        var r = await EnviarAsync<IdResposta>(HttpMethod.Post, "subscriptions", new
        {
            customer = clienteId,
            billingType = "UNDEFINED", // o psicólogo escolhe Pix, cartão ou boleto na fatura
            value = valor,
            nextDueDate = primeiroVencimento.ToString("yyyy-MM-dd"),
            cycle = "MONTHLY",
            description = descricao,
            externalReference = referencia
        });
        return r.Id;
    }

    public async Task<string?> UrlPrimeiraFaturaAsync(string assinaturaId)
    {
        var r = await EnviarAsync<ListaPagamentos>(HttpMethod.Get, $"subscriptions/{assinaturaId}/payments", null);
        return r.Data.Where(p => p.Status is "PENDING" or "OVERDUE").OrderBy(p => p.DueDate).FirstOrDefault()?.InvoiceUrl
               ?? r.Data.FirstOrDefault()?.InvoiceUrl;
    }

    public Task CancelarAssinaturaAsync(string assinaturaId) =>
        EnviarAsync<JsonElement>(HttpMethod.Delete, $"subscriptions/{assinaturaId}", null);

    private async Task<T> EnviarAsync<T>(HttpMethod metodo, string caminho, object? corpo)
    {
        using var req = new HttpRequestMessage(metodo, $"{opt.Value.BaseUrl.TrimEnd('/')}/{caminho}");
        req.Headers.Add("access_token", opt.Value.ApiKey);
        req.Headers.UserAgent.ParseAdd("Alento/1.0");
        if (corpo is not null) req.Content = JsonContent.Create(corpo, options: Json);

        var resp = await http.SendAsync(req);
        var texto = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var erro = TryErro(texto);
            throw new AsaasException(erro ?? $"Asaas respondeu {(int)resp.StatusCode}.");
        }
        return JsonSerializer.Deserialize<T>(texto, Json)!;
    }

    private static string? TryErro(string texto)
    {
        try
        {
            using var doc = JsonDocument.Parse(texto);
            return doc.RootElement.GetProperty("errors")[0].GetProperty("description").GetString();
        }
        catch { return null; }
    }

    private sealed record IdResposta(string Id);
    private sealed record ListaPagamentos(List<Pagamento> Data);
    private sealed record Pagamento(string Id, string Status, string? InvoiceUrl, string? DueDate);
}

/// <summary>Formato do evento de webhook do Asaas (só os campos usados).</summary>
public sealed record EventoAsaas(string? Id, string Event, PagamentoAsaas? Payment, AssinaturaAsaas? Subscription);
public sealed record PagamentoAsaas(string Id, string? Subscription, string? Status, string? DueDate, string? InvoiceUrl, string? ExternalReference);
public sealed record AssinaturaAsaas(string Id, string? Status, string? ExternalReference);
