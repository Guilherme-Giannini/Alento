using Alento.Web.Data;
using Alento.Web.Modules.Agenda;
using Alento.Web.Modules.Assinaturas;
using Alento.Web.Modules.Auditoria;
using Alento.Web.Modules.Contas;
using Alento.Web.Modules.Email;
using Alento.Web.Modules.Pacientes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Alento.Tests.Integracao;

/// <summary>
/// PostgreSQL real para os testes. Usa Testcontainers (Docker) por padrão; se a variável
/// ALENTO_TEST_DB apontar para um servidor, cria um banco temporário nele e apaga ao final.
/// </summary>
public sealed class BancoFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _servidorExterno;
    private string? _nomeBanco;

    public string ConnectionString { get; private set; } = "";
    public ServiceProvider Services { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        _servidorExterno = Environment.GetEnvironmentVariable("ALENTO_TEST_DB");
        if (!string.IsNullOrWhiteSpace(_servidorExterno))
        {
            _nomeBanco = "alento_testes_" + Guid.NewGuid().ToString("N")[..8];
            await using (var c = new NpgsqlConnection(_servidorExterno))
            {
                await c.OpenAsync();
                await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_nomeBanco}", c);
                await cmd.ExecuteNonQueryAsync();
            }
            ConnectionString = new NpgsqlConnectionStringBuilder(_servidorExterno) { Database = _nomeBanco }.ConnectionString;
        }
        else
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }

        var sc = new ServiceCollection();
        sc.AddLogging();
        // O modelo do Identity (tabela de passkeys) depende da versão de schema configurada, igual ao app.
        sc.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<AppDbContext>();
        sc.AddScoped<TenantContext>();
        sc.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(ConnectionString), ServiceLifetime.Scoped);
        sc.AddValidatorsFromAssemblyContaining<Program>();
        sc.AddScoped<ContaService>();
        sc.AddScoped<RegistroService>();
        sc.AddScoped<AuditoriaService>();
        sc.AddScoped<ConfiguracaoAgendaService>();
        sc.AddScoped<AgendaService>();
        sc.AddScoped<AgendamentoPublicoService>();
        sc.AddScoped<PacienteService>();
        sc.AddScoped<AsaasWebhookProcessor>();
        sc.AddScoped<INotificador, NotificadorNulo>();
        Services = sc.BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
        if (_servidorExterno is not null && _nomeBanco is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var c = new NpgsqlConnection(_servidorExterno);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_nomeBanco} WITH (FORCE)", c);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Escopo "logado" como o psicólogo informado (null = sem psicólogo, como um job).</summary>
    public AsyncServiceScope Escopo(Guid? tenantId)
    {
        var scope = Services.CreateAsyncScope();
        if (tenantId is { } id) scope.ServiceProvider.GetRequiredService<TenantContext>().Definir(id);
        return scope;
    }

    public async Task<Tenant> NovoPsicologoAsync(string nome)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RegistroService>().CriarTenantAsync(nome + " " + Guid.NewGuid().ToString("N")[..6], "06/0000", "11912345678");
    }

    private sealed class NotificadorNulo : INotificador
    {
        public Task NotificarPsicologoAsync(Guid tenantId, string assunto, string texto) => Task.CompletedTask;
    }
}

[CollectionDefinition(Nome)]
public class ColecaoBanco : ICollectionFixture<BancoFixture>
{
    public const string Nome = "banco";
}
