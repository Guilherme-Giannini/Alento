namespace Alento.Web.Modules.Agenda;

public readonly record struct Intervalo(DateTime InicioUtc, DateTime FimUtc)
{
    public bool Sobrepoe(DateTime inicio, DateTime fim) => InicioUtc < fim && inicio < FimUtc;
}

public readonly record struct JanelaSemanal(DayOfWeek Dia, TimeOnly Inicio, TimeOnly Fim);

public readonly record struct HorarioLivre(DateTime InicioUtc, DateTime FimUtc, DateTime InicioLocal);

public sealed record ParametrosHorarios
{
    public required TimeZoneInfo Fuso { get; init; }
    public required IReadOnlyList<JanelaSemanal> Disponibilidade { get; init; }
    public IReadOnlyList<Intervalo> Bloqueios { get; init; } = [];
    public IReadOnlyList<Intervalo> Ocupados { get; init; } = [];
    public required int DuracaoMinutos { get; init; }
    public int IntervaloMinutos { get; init; }
    public required DateOnly De { get; init; }
    public required DateOnly Ate { get; init; }
    public required DateTime AgoraUtc { get; init; }
    public TimeSpan AntecedenciaMinima { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// Horários livres = disponibilidade semanal − bloqueios − agendamentos existentes.
/// Função pura (sem banco) para ser testada à exaustão: é onde mais aparecem bugs.
/// </summary>
public static class CalculadoraHorarios
{
    public static IReadOnlyList<HorarioLivre> Calcular(ParametrosHorarios p)
    {
        if (p.DuracaoMinutos <= 0) throw new ArgumentOutOfRangeException(nameof(p), "Duração precisa ser positiva.");

        var duracao = TimeSpan.FromMinutes(p.DuracaoMinutos);
        var passo = TimeSpan.FromMinutes(p.DuracaoMinutos + Math.Max(0, p.IntervaloMinutos));
        var limiteInicio = p.AgoraUtc + p.AntecedenciaMinima;
        var resultado = new List<HorarioLivre>();

        for (var dia = p.De; dia <= p.Ate; dia = dia.AddDays(1))
        {
            var janelas = p.Disponibilidade.Where(j => j.Dia == dia.DayOfWeek && j.Fim > j.Inicio).OrderBy(j => j.Inicio);
            foreach (var janela in janelas)
            {
                var fimJanela = dia.ToDateTime(janela.Fim);
                for (var local = dia.ToDateTime(janela.Inicio); local + duracao <= fimJanela; local += passo)
                {
                    // Horário inexistente (início do horário de verão) é pulado.
                    if (p.Fuso.IsInvalidTime(local)) continue;

                    var inicioUtc = global::Alento.Web.Fuso.ParaUtc(local, p.Fuso);
                    var fimUtc = inicioUtc + duracao;
                    if (inicioUtc < limiteInicio) continue;
                    if (p.Bloqueios.Any(b => b.Sobrepoe(inicioUtc, fimUtc))) continue;
                    if (p.Ocupados.Any(o => o.Sobrepoe(inicioUtc, fimUtc))) continue;

                    resultado.Add(new HorarioLivre(inicioUtc, fimUtc, local));
                }
            }
        }

        return resultado.DistinctBy(h => h.InicioUtc).OrderBy(h => h.InicioUtc).ToList();
    }
}
