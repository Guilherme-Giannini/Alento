using Alento.Web.Data;
using Alento.Web.Modules.Contas;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Alento.Web.Modules.Agenda;

public class HorarioIndisponivelException()
    : RegraNegocioException("Esse horário acabou de ser ocupado. Escolha outro, por favor.");

public static class ConflitoAgenda
{
    /// <summary>A restrição de exclusão do PostgreSQL é a garantia final contra dois pacientes no mesmo horário.</summary>
    public static bool EhSobreposicao(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } pg
        && pg.ConstraintName == AppDbContext.ConstraintSemSobreposicao;

    public static async Task SalvarAsync(AppDbContext db, CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (EhSobreposicao(ex))
        {
            throw new HorarioIndisponivelException();
        }
    }

    /// <summary>Estados que ocupam o horário (e entram na restrição do banco).</summary>
    public static bool Ocupa(StatusAgendamento s) => s != StatusAgendamento.Cancelado;
}
