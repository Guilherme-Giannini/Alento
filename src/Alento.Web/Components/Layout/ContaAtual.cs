using Alento.Web.Data;
using Alento.Web.Modules.Contas;

namespace Alento.Web.Components.Layout;

/// <summary>Estado da conta do psicólogo, cascateado pelo AppLayout para todas as telas do painel.</summary>
public sealed record ContaAtual(Tenant Tenant, NivelAcesso Nivel, int DiasRestantes, Func<Task> Recarregar)
{
    public TimeZoneInfo Fuso => Web.Fuso.Obter(Tenant.FusoHorario);
    public bool PodeEditar => Nivel == NivelAcesso.Total;
    public DateTime Local(DateTime utc) => Web.Fuso.ParaLocal(utc, Fuso);
    public DateTime Utc(DateTime local) => Web.Fuso.ParaUtc(local, Fuso);
    public DateOnly Hoje => Web.Fuso.Hoje(Fuso);
}
