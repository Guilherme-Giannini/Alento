using Alento.Web;
using Alento.Web.Modules.Agenda;

namespace Alento.Tests;

public class CalculadoraHorariosTests
{
    private static readonly TimeZoneInfo SaoPaulo = Fuso.Obter("America/Sao_Paulo");

    // 2026-10-05 é uma segunda-feira.
    private static readonly DateOnly Segunda = new(2026, 10, 5);

    private static ParametrosHorarios Base(params JanelaSemanal[] janelas) => new()
    {
        Fuso = SaoPaulo,
        Disponibilidade = janelas.Length > 0 ? janelas : [new JanelaSemanal(DayOfWeek.Monday, new(8, 0), new(12, 0))],
        DuracaoMinutos = 50,
        IntervaloMinutos = 10,
        De = Segunda,
        Ate = Segunda,
        AgoraUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
    };

    private static DateTime Utc(DateOnly dia, int hora, int minuto = 0) => Fuso.ParaUtc(dia, new TimeOnly(hora, minuto), SaoPaulo);

    [Fact]
    public void Gera_horarios_a_cada_duracao_mais_intervalo_dentro_da_janela()
    {
        var livres = CalculadoraHorarios.Calcular(Base());

        Assert.Equal(["08:00", "09:00", "10:00", "11:00"], livres.Select(h => h.InicioLocal.ToString("HH:mm")));
        Assert.All(livres, h => Assert.Equal(TimeSpan.FromMinutes(50), h.FimUtc - h.InicioUtc));
    }

    [Fact]
    public void Sessao_que_nao_cabe_no_fim_da_janela_nao_aparece()
    {
        var p = Base(new JanelaSemanal(DayOfWeek.Monday, new(8, 0), new(9, 30)));
        var livres = CalculadoraHorarios.Calcular(p);

        Assert.Equal(["08:00"], livres.Select(h => h.InicioLocal.ToString("HH:mm")));
    }

    [Fact]
    public void Converte_horario_local_para_utc_pelo_fuso_do_psicologo()
    {
        var livres = CalculadoraHorarios.Calcular(Base());

        // Brasília é UTC-3 (sem horário de verão desde 2019).
        Assert.Equal(new DateTime(2026, 10, 5, 11, 0, 0, DateTimeKind.Utc), livres[0].InicioUtc);
    }

    [Fact]
    public void Agendamento_existente_remove_apenas_os_horarios_sobrepostos()
    {
        var p = Base() with { Ocupados = [new Intervalo(Utc(Segunda, 9), Utc(Segunda, 9, 50))] };
        var livres = CalculadoraHorarios.Calcular(p);

        Assert.Equal(["08:00", "10:00", "11:00"], livres.Select(h => h.InicioLocal.ToString("HH:mm")));
    }

    [Fact]
    public void Agendamento_fora_da_grade_bloqueia_horarios_que_ele_toca()
    {
        // Sessão 08:30–09:20 (marcada manualmente) derruba 08:00 e 09:00.
        var p = Base() with { Ocupados = [new Intervalo(Utc(Segunda, 8, 30), Utc(Segunda, 9, 20))] };
        var livres = CalculadoraHorarios.Calcular(p);

        Assert.Equal(["10:00", "11:00"], livres.Select(h => h.InicioLocal.ToString("HH:mm")));
    }

    [Fact]
    public void Fim_de_sessao_encostando_no_inicio_da_outra_nao_e_conflito()
    {
        var p = Base() with { Ocupados = [new Intervalo(Utc(Segunda, 7, 10), Utc(Segunda, 8, 0))] };

        Assert.Contains(CalculadoraHorarios.Calcular(p), h => h.InicioLocal.Hour == 8);
    }

    [Fact]
    public void Bloqueio_de_ferias_remove_todos_os_horarios_do_periodo()
    {
        var p = Base() with
        {
            Ate = Segunda.AddDays(7),
            Bloqueios = [new Intervalo(Utc(Segunda, 0), Utc(Segunda.AddDays(1), 0))]
        };
        var livres = CalculadoraHorarios.Calcular(p);

        Assert.DoesNotContain(livres, h => DateOnly.FromDateTime(h.InicioLocal) == Segunda);
        Assert.Contains(livres, h => DateOnly.FromDateTime(h.InicioLocal) == Segunda.AddDays(7));
    }

    [Fact]
    public void Antecedencia_minima_esconde_horarios_muito_proximos()
    {
        var p = Base() with
        {
            AgoraUtc = Utc(Segunda, 7), // 07:00 local
            AntecedenciaMinima = TimeSpan.FromHours(2)
        };
        var livres = CalculadoraHorarios.Calcular(p);

        Assert.Equal(["09:00", "10:00", "11:00"], livres.Select(h => h.InicioLocal.ToString("HH:mm")));
    }

    [Fact]
    public void Horarios_no_passado_nunca_aparecem()
    {
        var p = Base() with { AgoraUtc = Utc(Segunda, 10, 30) };

        Assert.Equal(["11:00"], CalculadoraHorarios.Calcular(p).Select(h => h.InicioLocal.ToString("HH:mm")));
    }

    [Fact]
    public void Dias_sem_disponibilidade_ficam_vazios()
    {
        var p = Base() with { De = Segunda.AddDays(1), Ate = Segunda.AddDays(6) };

        Assert.Empty(CalculadoraHorarios.Calcular(p));
    }

    [Fact]
    public void Varias_janelas_no_mesmo_dia_sao_respeitadas_e_ordenadas()
    {
        var p = Base(
            new JanelaSemanal(DayOfWeek.Monday, new(14, 0), new(16, 0)),
            new JanelaSemanal(DayOfWeek.Monday, new(8, 0), new(10, 0)));
        var livres = CalculadoraHorarios.Calcular(p);

        Assert.Equal(["08:00", "09:00", "14:00", "15:00"], livres.Select(h => h.InicioLocal.ToString("HH:mm")));
    }

    [Fact]
    public void Janelas_sobrepostas_nao_duplicam_horarios()
    {
        var p = Base(
            new JanelaSemanal(DayOfWeek.Monday, new(8, 0), new(10, 0)),
            new JanelaSemanal(DayOfWeek.Monday, new(8, 0), new(10, 0)));

        Assert.Equal(2, CalculadoraHorarios.Calcular(p).Count);
    }

    [Fact]
    public void Fuso_diferente_muda_o_utc_mas_nao_o_horario_local()
    {
        var manaus = Fuso.Obter("America/Manaus");
        var livres = CalculadoraHorarios.Calcular(Base() with { Fuso = manaus });

        Assert.Equal("08:00", livres[0].InicioLocal.ToString("HH:mm"));
        Assert.Equal(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), livres[0].InicioUtc);
    }

    [Fact]
    public void Duracao_invalida_e_rejeitada()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CalculadoraHorarios.Calcular(Base() with { DuracaoMinutos = 0 }));
    }
}
