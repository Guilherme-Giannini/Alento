using System.Text.Json;
using Alento.Web.Data;
using Alento.Web.Modules.Auditoria;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Pacientes;

public static class ExportacaoEndpoints
{
    public static void MapExportacoes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/app/exportar").RequireAuthorization();

        g.MapGet("/paciente/{id:guid}", async (Guid id, PacienteService pacientes) =>
        {
            var (nome, conteudo) = await pacientes.ExportarAsync(id);
            return Results.File(conteudo, "application/json", nome);
        });

        // Exportação completa: disponível mesmo com a conta bloqueada ou cancelada (guarda de 5 anos do CFP).
        g.MapGet("/tudo", async (IDbContextFactory<AppDbContext> dbf, AuditoriaService auditoria) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var dados = new
            {
                exportadoEm = DateTime.UtcNow,
                pacientes = await db.Pacientes.AsNoTracking().OrderBy(p => p.Nome)
                    .Select(p => new { p.Id, p.Nome, p.WhatsApp, p.Email, p.ConsentimentoMensagens, p.ConsentimentoEm, p.Observacoes, p.CriadoEm })
                    .ToListAsync(),
                sessoes = await db.Agendamentos.AsNoTracking().OrderBy(a => a.Inicio)
                    .Select(a => new
                    {
                        a.Id, a.PacienteId, paciente = a.Paciente!.Nome, servico = a.Servico!.Nome, a.Inicio, a.Fim,
                        status = a.Status.ToString(), a.Valor, a.LinkVideo,
                        cobranca = a.Cobranca == null ? null : new { status = a.Cobranca.Status.ToString(), a.Cobranca.PagoEm }
                    }).ToListAsync()
            };
            await auditoria.RegistrarAsync("exportou-tudo", "Conta");
            var json = JsonSerializer.SerializeToUtf8Bytes(dados, new JsonSerializerOptions { WriteIndented = true });
            return Results.File(json, "application/json", $"alento-backup-{DateTime.UtcNow:yyyy-MM-dd}.json");
        });
    }
}
