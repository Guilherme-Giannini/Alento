using Alento.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Modules.Auditoria;

/// <summary>Registro de quem viu ou alterou dados de pacientes (LGPD, art. 37).</summary>
public class AuditoriaService(IDbContextFactory<AppDbContext> dbf, TenantContext tenant)
{
    public async Task RegistrarAsync(string acao, string entidade, object? entidadeId = null)
    {
        await using var db = await dbf.CreateDbContextAsync();
        db.Auditorias.Add(new Data.Auditoria
        {
            TenantId = tenant.TenantIdObrigatorio,
            Usuario = tenant.NomeUsuario,
            Acao = acao,
            Entidade = entidade,
            EntidadeId = entidadeId?.ToString()
        });
        await db.SaveChangesAsync();
    }

    public async Task<List<Data.Auditoria>> ListarAsync(int limite = 200)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Auditorias.AsNoTracking().OrderByDescending(a => a.Data).Take(limite).ToListAsync();
    }
}
