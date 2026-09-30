using System.Globalization;
using Alento.Web.Components;
using Alento.Web.Components.Account;
using Alento.Web.Data;
using Alento.Web.Infra;
using Alento.Web.Modules.Agenda;
using Alento.Web.Modules.Assinaturas;
using Alento.Web.Modules.Auditoria;
using Alento.Web.Modules.Cobrancas;
using Alento.Web.Modules.Contas;
using Alento.Web.Modules.Email;
using Alento.Web.Modules.Lembretes;
using Alento.Web.Modules.Painel;
using Alento.Web.Modules.Pacientes;
using Alento.Web.Modules.Webhooks;
using FluentValidation;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using Serilog;

var ptBr = CultureInfo.GetCultureInfo("pt-BR");
CultureInfo.DefaultThreadCurrentCulture = ptBr;
CultureInfo.DefaultThreadCurrentUICulture = ptBr;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));
if (!string.IsNullOrWhiteSpace(builder.Configuration["Sentry:Dsn"]))
    builder.WebHost.UseSentry();

// Blazor: painel interativo no servidor; landing, página pública e contas em SSR estático.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices(c =>
{
    c.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
    c.SnackbarConfiguration.VisibleStateDuration = 3500;
});

// Autenticação (ASP.NET Core Identity com cookie).
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = IdentityConstants.ApplicationScheme;
        o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies(c => c.ApplicationCookie?.Configure(o =>
    {
        o.LoginPath = "/Account/Login";
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Cookie.Name = "alento.auth";
    }));
builder.Services.AddAuthorization();

// Banco: PostgreSQL + EF Core, com filtro global por psicólogo (TenantId).
var conn = builder.Configuration.GetConnectionString("DefaultConnection")
           ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não encontrada.");
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(conn), ServiceLifetime.Scoped);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(o =>
    {
        o.SignIn.RequireConfirmedAccount = false; // entra direto no teste; confirmação de e-mail segue em paralelo
        o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AlentoClaimsFactory>()
    .AddErrorDescriber<IdentityErrosPt>();

// Módulos (monólito modular: uma pasta por módulo, conversando por serviços).
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddScoped<ContaService>();
builder.Services.AddScoped<RegistroService>();
builder.Services.AddScoped<AuditoriaService>();
builder.Services.AddScoped<ConfiguracaoAgendaService>();
builder.Services.AddScoped<AgendaService>();
builder.Services.AddScoped<AgendamentoPublicoService>();
builder.Services.AddScoped<PacienteService>();
builder.Services.AddScoped<CobrancaService>();
builder.Services.AddScoped<PainelService>();
builder.Services.AddScoped<AssinaturaService>();
builder.Services.AddScoped<AsaasWebhookProcessor>();
builder.Services.AddScoped<LembreteService>();
builder.Services.AddScoped<INotificador, NotificadorEmail>();
builder.Services.AddScoped<Jobs>();

// Integrações HTTP com retentativa (Microsoft.Extensions.Http.Resilience / Polly).
builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection("WhatsApp"));
builder.Services.Configure<AsaasOptions>(builder.Configuration.GetSection("Asaas"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddHttpClient<IWhatsAppClient, WhatsAppClient>().AddStandardResilienceHandler();
builder.Services.AddHttpClient<AsaasClient>().AddStandardResilienceHandler();
builder.Services.AddHttpClient<IEmailService, ResendEmailService>().AddStandardResilienceHandler();
builder.Services.AddScoped<IEmailSender<ApplicationUser>, IdentityEmailSender>();

// Jobs (Hangfire com armazenamento no PostgreSQL).
var hangfireAtivo = builder.Configuration.GetValue("Hangfire:Ativo", true);
builder.Services.AddHangfire(c => c
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(conn), new PostgreSqlStorageOptions { SchemaName = "hangfire" }));
if (hangfireAtivo) builder.Services.AddHangfireServer(o => o.WorkerCount = 2);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddHealthChecks();

// Chaves do Data Protection em volume: cookies de login sobrevivem a deploys e reinícios do container.
var pastaChaves = builder.Configuration["DataProtection:Pasta"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Alento");
if (!string.IsNullOrWhiteSpace(pastaChaves))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(pastaChaves));

var app = builder.Build();

// Migrations na subida: um servidor, um banco, deploy sem passo manual.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.UseForwardedHeaders();
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseSerilogRequestLogging();
// Página amigável de 404 só para navegação; webhooks e downloads devolvem o status puro.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/webhooks") && !ctx.Request.Path.StartsWithSegments("/app/exportar"),
    b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapWebhooks();
app.MapExportacoes();

var admins = builder.Configuration.GetSection("Alento:Admins").Get<string[]>() ?? [];
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new HangfireAdminFilter(admins)],
    DashboardTitle = "Alento · Jobs"
});
if (hangfireAtivo) Jobs.Registrar(app.Services.GetRequiredService<IRecurringJobManager>());

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapAdditionalIdentityEndpoints();

app.Run();

public partial class Program;
