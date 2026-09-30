using Alento.Web.Data;
using Alento.Web.Modules.Agenda;
using Alento.Web.Modules.Assinaturas;
using Alento.Web.Modules.Contas;
using Alento.Web.Modules.Pacientes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alento.Tests.Integracao;

[Collection(ColecaoBanco.Nome)]
public class IsolamentoETenantTests(BancoFixture banco)
{
    private static T S<T>(AsyncServiceScope s) where T : notnull => s.ServiceProvider.GetRequiredService<T>();

    private async Task<Guid> NovoPacienteAsync(Guid tenant, string nome, string whats)
    {
        await using var s = banco.Escopo(tenant);
        return await S<PacienteService>(s).SalvarAsync(new Paciente { Nome = nome, WhatsApp = whats, ConsentimentoMensagens = true, Ativo = true });
    }

    [Fact]
    public async Task Psicologo_nao_enxerga_pacientes_de_outro_psicologo()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var bruno = await banco.NovoPsicologoAsync("Bruno");
        var idPacienteAna = await NovoPacienteAsync(ana.Id, "Paciente da Ana", "11911112222");

        await using var s = banco.Escopo(bruno.Id);
        var lista = await S<PacienteService>(s).ListarAsync(incluirInativos: true);
        var detalhe = await S<PacienteService>(s).DetalheAsync(idPacienteAna);

        Assert.DoesNotContain(lista, p => p.Id == idPacienteAna);
        Assert.Null(detalhe);
    }

    [Fact]
    public async Task Consulta_direta_no_dbcontext_tambem_e_filtrada()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var bruno = await banco.NovoPsicologoAsync("Bruno");
        await NovoPacienteAsync(ana.Id, "Paciente A", "11933334444");

        await using var s = banco.Escopo(bruno.Id);
        await using var db = await S<IDbContextFactory<AppDbContext>>(s).CreateDbContextAsync();

        Assert.False(await db.Pacientes.AnyAsync(p => p.TenantId == ana.Id));
        Assert.False(await db.Servicos.AnyAsync(p => p.TenantId == ana.Id));
    }

    [Fact]
    public async Task Sem_psicologo_ativo_nenhum_dado_de_negocio_aparece()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        await NovoPacienteAsync(ana.Id, "Paciente A", "11955556666");

        await using var s = banco.Escopo(null);
        await using var db = await S<IDbContextFactory<AppDbContext>>(s).CreateDbContextAsync();

        Assert.Empty(await db.Pacientes.ToListAsync());
    }

    [Fact]
    public async Task Alterar_registro_de_outro_psicologo_e_bloqueado_no_SaveChanges()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var bruno = await banco.NovoPsicologoAsync("Bruno");
        var id = await NovoPacienteAsync(ana.Id, "Paciente A", "11977778888");

        await using var s = banco.Escopo(bruno.Id);
        await using var db = await S<IDbContextFactory<AppDbContext>>(s).CreateDbContextAsync();
        var p = await db.Pacientes.IgnoreQueryFilters().FirstAsync(x => x.Id == id);
        p.Nome = "invadido";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Dois_agendamentos_no_mesmo_horario_sao_impedidos_pelo_banco()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var p1 = await NovoPacienteAsync(ana.Id, "P1", "11910000001");
        var p2 = await NovoPacienteAsync(ana.Id, "P2", "11910000002");
        var inicio = DateTime.UtcNow.Date.AddDays(3).AddHours(15);

        await using var s = banco.Escopo(ana.Id);
        var agenda = S<AgendaService>(s);
        var servico = (await S<ConfiguracaoAgendaService>(s).ServicosAsync()).First();

        await agenda.CriarAsync(new NovoAgendamento { PacienteId = p1, ServicoId = servico.Id, InicioUtc = inicio });
        await Assert.ThrowsAsync<HorarioIndisponivelException>(() =>
            agenda.CriarAsync(new NovoAgendamento { PacienteId = p2, ServicoId = servico.Id, InicioUtc = inicio.AddMinutes(20) }));
    }

    [Fact]
    public async Task Reservas_simultaneas_do_mesmo_horario_so_uma_vence()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var p1 = await NovoPacienteAsync(ana.Id, "P1", "11920000001");
        var p2 = await NovoPacienteAsync(ana.Id, "P2", "11920000002");
        var inicio = DateTime.UtcNow.Date.AddDays(4).AddHours(15);
        Guid servicoId;
        await using (var s0 = banco.Escopo(ana.Id))
            servicoId = (await S<ConfiguracaoAgendaService>(s0).ServicosAsync()).First().Id;

        async Task<bool> Tentar(Guid paciente)
        {
            await using var s = banco.Escopo(ana.Id);
            try
            {
                await S<AgendaService>(s).CriarAsync(new NovoAgendamento { PacienteId = paciente, ServicoId = servicoId, InicioUtc = inicio });
                return true;
            }
            catch (HorarioIndisponivelException) { return false; }
        }

        var resultados = await Task.WhenAll(Tentar(p1), Tentar(p2));

        Assert.Single(resultados, r => r);
    }

    [Fact]
    public async Task Horario_cancelado_fica_livre_de_novo()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var p1 = await NovoPacienteAsync(ana.Id, "P1", "11930000001");
        var inicio = DateTime.UtcNow.Date.AddDays(5).AddHours(15);

        await using var s = banco.Escopo(ana.Id);
        var agenda = S<AgendaService>(s);
        var servico = (await S<ConfiguracaoAgendaService>(s).ServicosAsync()).First();
        var id = await agenda.CriarAsync(new NovoAgendamento { PacienteId = p1, ServicoId = servico.Id, InicioUtc = inicio });
        await agenda.AlterarStatusAsync(id, StatusAgendamento.Cancelado);

        var novo = await agenda.CriarAsync(new NovoAgendamento { PacienteId = p1, ServicoId = servico.Id, InicioUtc = inicio });
        Assert.NotEqual(Guid.Empty, novo);
    }

    [Fact]
    public async Task Psicologos_diferentes_podem_ter_sessao_no_mesmo_horario()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var bruno = await banco.NovoPsicologoAsync("Bruno");
        var inicio = DateTime.UtcNow.Date.AddDays(6).AddHours(15);

        foreach (var t in new[] { ana.Id, bruno.Id })
        {
            var p = await NovoPacienteAsync(t, "P", "11940000001");
            await using var s = banco.Escopo(t);
            var servico = (await S<ConfiguracaoAgendaService>(s).ServicosAsync()).First();
            await S<AgendaService>(s).CriarAsync(new NovoAgendamento { PacienteId = p, ServicoId = servico.Id, InicioUtc = inicio });
        }
    }

    [Fact]
    public async Task Sessao_realizada_gera_cobranca_com_o_valor_da_sessao()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var p = await NovoPacienteAsync(ana.Id, "P", "11950000001");

        await using var s = banco.Escopo(ana.Id);
        var servico = (await S<ConfiguracaoAgendaService>(s).ServicosAsync()).First();
        var id = await S<AgendaService>(s).CriarAsync(new NovoAgendamento { PacienteId = p, ServicoId = servico.Id, InicioUtc = DateTime.UtcNow.AddDays(-1) });
        await S<AgendaService>(s).AlterarStatusAsync(id, StatusAgendamento.Realizado);

        await using var db = await S<IDbContextFactory<AppDbContext>>(s).CreateDbContextAsync();
        var cobranca = await db.Cobrancas.SingleAsync(c => c.AgendamentoId == id);
        Assert.Equal(servico.Valor, cobranca.Valor);
        Assert.Equal(StatusCobranca.Pendente, cobranca.Status);
    }

    [Fact]
    public async Task Recorrencia_semanal_pula_semana_bloqueada()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var p = await NovoPacienteAsync(ana.Id, "P", "11960000001");
        var tz = Alento.Web.Fuso.Obter(ana.FusoHorario);
        var inicio = Alento.Web.Fuso.Hoje(tz).AddDays(1);

        await using var s = banco.Escopo(ana.Id);
        var servico = (await S<ConfiguracaoAgendaService>(s).ServicosAsync()).First();
        // Bloqueia a segunda ocorrência (semana seguinte à primeira).
        var segunda = inicio.AddDays(7);
        await S<ConfiguracaoAgendaService>(s).AdicionarBloqueioAsync(new Bloqueio
        {
            Inicio = Alento.Web.Fuso.ParaUtc(segunda, TimeOnly.MinValue, tz),
            Fim = Alento.Web.Fuso.ParaUtc(segunda.AddDays(1), TimeOnly.MinValue, tz),
            Motivo = "Congresso"
        });

        var (criadas, puladas) = await S<AgendaService>(s).CriarRecorrenciaAsync(new NovaRecorrencia
        {
            PacienteId = p, ServicoId = servico.Id, DiaDaSemana = inicio.DayOfWeek, Horario = new TimeOnly(18, 0), InicioEm = inicio
        });

        Assert.Equal(1, puladas);
        Assert.True(criadas >= 7);
    }

    [Fact]
    public async Task Conta_cancelada_fica_somente_leitura()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        await using var s = banco.Escopo(ana.Id);
        await using (var db = await S<IDbContextFactory<AppDbContext>>(s).CreateDbContextAsync())
        {
            await db.Tenants.Where(t => t.Id == ana.Id).ExecuteUpdateAsync(x => x.SetProperty(t => t.StatusAssinatura, StatusAssinatura.Cancelada));
        }

        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            S<PacienteService>(s).SalvarAsync(new Paciente { Nome = "Novo", WhatsApp = "11970000001" }));
        Assert.NotNull(await S<PacienteService>(s).ListarAsync());
    }

    [Fact]
    public async Task Webhook_do_asaas_ativa_bloqueia_e_ignora_eventos_repetidos()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        await using (var s0 = banco.Escopo(ana.Id))
        await using (var db = await S<IDbContextFactory<AppDbContext>>(s0).CreateDbContextAsync())
        {
            await db.Assinaturas.Where(a => a.TenantId == ana.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.IdAsaas, "sub_" + ana.Id.ToString("N")));
        }
        var sub = "sub_" + ana.Id.ToString("N");

        async Task<StatusAssinatura> Processar(string idEvento, string evento)
        {
            await using var s = banco.Escopo(null);
            await S<AsaasWebhookProcessor>(s).ProcessarAsync(new EventoAsaas(idEvento, evento,
                new PagamentoAsaas("pay_1", sub, "CONFIRMED", "2026-10-10", "https://fatura", null), null));
            await using var db = await S<IDbContextFactory<AppDbContext>>(s).CreateDbContextAsync();
            return (await db.Tenants.FirstAsync(t => t.Id == ana.Id)).StatusAssinatura;
        }

        Assert.Equal(StatusAssinatura.Ativa, await Processar("evt_1_" + ana.Id, "PAYMENT_CONFIRMED"));
        Assert.Equal(StatusAssinatura.Inadimplente, await Processar("evt_2_" + ana.Id, "PAYMENT_OVERDUE"));
        // Reenvio do evento 1 não pode reativar a conta.
        Assert.Equal(StatusAssinatura.Inadimplente, await Processar("evt_1_" + ana.Id, "PAYMENT_CONFIRMED"));
    }

    [Fact]
    public async Task Exclusao_lgpd_remove_paciente_e_todas_as_sessoes()
    {
        var ana = await banco.NovoPsicologoAsync("Ana");
        var p = await NovoPacienteAsync(ana.Id, "P", "11980000001");

        await using var s = banco.Escopo(ana.Id);
        var servico = (await S<ConfiguracaoAgendaService>(s).ServicosAsync()).First();
        var ag = await S<AgendaService>(s).CriarAsync(new NovoAgendamento { PacienteId = p, ServicoId = servico.Id, InicioUtc = DateTime.UtcNow.AddDays(-2) });
        await S<AgendaService>(s).AlterarStatusAsync(ag, StatusAgendamento.Realizado);

        var (_, json) = await S<PacienteService>(s).ExportarAsync(p);
        Assert.Contains("\"sessoes\"", System.Text.Encoding.UTF8.GetString(json));

        await S<PacienteService>(s).ExcluirAsync(p);
        await using var db = await S<IDbContextFactory<AppDbContext>>(s).CreateDbContextAsync();
        Assert.False(await db.Pacientes.AnyAsync(x => x.Id == p));
        Assert.False(await db.Agendamentos.AnyAsync(x => x.PacienteId == p));
        Assert.True(await db.Auditorias.AnyAsync(a => a.EntidadeId == p.ToString() && a.Acao == "excluiu"));
    }
}
