using System.Globalization;
using Alento.Web.Data;
using Alento.Web.Modules.Contas;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Assinaturas;

public record DadosCobrancaSaaS(string NomeCompleto, string CpfCnpj, string? Celular);

public class AssinaturaService(
    IDbContextFactory<AppDbContext> dbf,
    TenantContext tenant,
    AsaasClient asaas,
    IWebHostEnvironment env,
    ILogger<AssinaturaService> log)
{
    public async Task<Assinatura> AtualAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Assinaturas.AsNoTracking().FirstOrDefaultAsync()
               ?? new Assinatura { TenantId = tenant.TenantIdObrigatorio, Valor = PlanoAlento.ValorMensal };
    }

    /// <summary>
    /// Cria cliente + assinatura mensal no Asaas e devolve a URL da fatura.
    /// Durante o teste, o primeiro vencimento é o fim do teste: o psicólogo não perde dias grátis ao assinar cedo.
    /// </summary>
    public async Task<string> AssinarAsync(string email, DadosCobrancaSaaS dados)
    {
        var doc = new string(dados.CpfCnpj.Where(char.IsDigit).ToArray());
        if (doc.Length is not (11 or 14)) throw new RegraNegocioException("Informe um CPF ou CNPJ válido.");

        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.Tenants.FirstAsync(x => x.Id == tenant.TenantIdObrigatorio);
        var a = await db.Assinaturas.FirstOrDefaultAsync();
        if (a is null)
        {
            a = new Assinatura { Valor = PlanoAlento.ValorMensal };
            db.Assinaturas.Add(a);
        }

        if (!asaas.Configurado)
        {
            if (!env.IsDevelopment()) throw new RegraNegocioException("Pagamentos indisponíveis no momento. Tente novamente em instantes.");
            // Desenvolvimento: sem chave do Asaas, a assinatura é ativada na hora para testar o fluxo.
            log.LogWarning("Asaas não configurado: ativando assinatura simulada para {Tenant}", t.Id);
            a.Status = t.StatusAssinatura = StatusAssinatura.Ativa;
            a.ProximoVencimento = DateTime.UtcNow.AddMonths(1);
            a.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return "/app/assinatura?sucesso=true";
        }

        try
        {
            a.IdClienteAsaas ??= await asaas.CriarClienteAsync(dados.NomeCompleto, email, doc, dados.Celular, t.Id.ToString());
            if (a.IdAsaas is null || a.Status == StatusAssinatura.Cancelada)
            {
                var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
                var vencimento = t.StatusAssinatura == StatusAssinatura.Teste && t.TesteTerminaEm > DateTime.UtcNow
                    ? DateOnly.FromDateTime(t.TesteTerminaEm)
                    : hoje;
                a.IdAsaas = await asaas.CriarAssinaturaAsync(a.IdClienteAsaas, PlanoAlento.ValorMensal, vencimento,
                    $"{PlanoAlento.Nome} - mensal", t.Id.ToString());
                a.ProximoVencimento = vencimento.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            }
            a.UrlUltimaFatura = await asaas.UrlPrimeiraFaturaAsync(a.IdAsaas) ?? a.UrlUltimaFatura;
            a.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return a.UrlUltimaFatura ?? "/app/assinatura";
        }
        catch (AsaasException ex)
        {
            await db.SaveChangesAsync(); // guarda o cliente criado para não duplicar na próxima tentativa
            throw new RegraNegocioException($"Não foi possível criar a assinatura: {ex.Message}");
        }
    }

    public async Task CancelarAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.Tenants.FirstAsync(x => x.Id == tenant.TenantIdObrigatorio);
        var a = await db.Assinaturas.FirstOrDefaultAsync();
        if (a?.IdAsaas is not null && asaas.Configurado) await asaas.CancelarAssinaturaAsync(a.IdAsaas);
        if (a is not null) { a.Status = StatusAssinatura.Cancelada; a.AtualizadoEm = DateTime.UtcNow; }
        t.StatusAssinatura = StatusAssinatura.Cancelada;
        await db.SaveChangesAsync();
    }
}

/// <summary>Processa os webhooks do Asaas: pago libera, vencido bloqueia, cancelado vira somente leitura.</summary>
public class AsaasWebhookProcessor(IDbContextFactory<AppDbContext> dbf, ILogger<AsaasWebhookProcessor> log)
{
    public async Task ProcessarAsync(EventoAsaas evento)
    {
        await using var db = await dbf.CreateDbContextAsync();

        // Idempotência: o Asaas reenvia eventos; cada id é processado uma vez só.
        var idEvento = evento.Id ?? $"{evento.Event}:{evento.Payment?.Id ?? evento.Subscription?.Id}";
        if (await db.WebhookEventos.AnyAsync(e => e.Id == idEvento)) return;
        db.WebhookEventos.Add(new WebhookEvento { Id = idEvento, Origem = "asaas" });

        var idAssinatura = evento.Payment?.Subscription ?? evento.Subscription?.Id;
        var referencia = evento.Payment?.ExternalReference ?? evento.Subscription?.ExternalReference;

        var a = idAssinatura is null ? null
            : await db.Assinaturas.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.IdAsaas == idAssinatura);
        if (a is null && Guid.TryParse(referencia, out var tid))
            a = await db.Assinaturas.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TenantId == tid);
        if (a is null)
        {
            log.LogWarning("Webhook Asaas {Evento} sem assinatura correspondente ({Id})", evento.Event, idAssinatura);
            await db.SaveChangesAsync();
            return;
        }

        var t = await db.Tenants.FirstAsync(x => x.Id == a.TenantId);
        StatusAssinatura? novo = evento.Event switch
        {
            "PAYMENT_CONFIRMED" or "PAYMENT_RECEIVED" => StatusAssinatura.Ativa,
            "PAYMENT_OVERDUE" => StatusAssinatura.Inadimplente,
            "SUBSCRIPTION_DELETED" or "SUBSCRIPTION_INACTIVATED" => StatusAssinatura.Cancelada,
            _ => null
        };

        if (novo == StatusAssinatura.Ativa && DateOnly.TryParse(evento.Payment?.DueDate, CultureInfo.InvariantCulture, out var venc))
            a.ProximoVencimento = venc.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (evento.Payment?.InvoiceUrl is { } url) a.UrlUltimaFatura = url;

        // Pagamento atrasado de uma assinatura que já foi cancelada não reabre a conta.
        if (novo is { } s && !(t.StatusAssinatura == StatusAssinatura.Cancelada && s == StatusAssinatura.Inadimplente))
        {
            a.Status = t.StatusAssinatura = s;
            log.LogInformation("Assinatura do tenant {Tenant} agora está {Status} ({Evento})", t.Id, s, evento.Event);
        }
        a.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
