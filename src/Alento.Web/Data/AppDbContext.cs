using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Alento.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant)
    : IdentityDbContext<ApplicationUser>(options)
{
    public const string ConstraintSemSobreposicao = "ex_agendamentos_sem_sobreposicao";

    /// <summary>Lido pelo filtro global a cada consulta. Sem psicólogo ativo, nenhuma linha de negócio aparece.</summary>
    public Guid TenantAtual => tenant.TenantId ?? Guid.Empty;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Servico> Servicos => Set<Servico>();
    public DbSet<Disponibilidade> Disponibilidades => Set<Disponibilidade>();
    public DbSet<Bloqueio> Bloqueios => Set<Bloqueio>();
    public DbSet<Paciente> Pacientes => Set<Paciente>();
    public DbSet<Recorrencia> Recorrencias => Set<Recorrencia>();
    public DbSet<Agendamento> Agendamentos => Set<Agendamento>();
    public DbSet<Mensagem> Mensagens => Set<Mensagem>();
    public DbSet<Cobranca> Cobrancas => Set<Cobranca>();
    public DbSet<Assinatura> Assinaturas => Set<Assinatura>();
    public DbSet<Auditoria> Auditorias => Set<Auditoria>();
    public DbSet<WebhookEvento> WebhookEventos => Set<WebhookEvento>();

    protected override void ConfigureConventions(ModelConfigurationBuilder cfg)
    {
        cfg.Properties<StatusAssinatura>().HaveConversion<string>().HaveMaxLength(20);
        cfg.Properties<StatusAgendamento>().HaveConversion<string>().HaveMaxLength(20);
        cfg.Properties<StatusMensagem>().HaveConversion<string>().HaveMaxLength(20);
        cfg.Properties<StatusCobranca>().HaveConversion<string>().HaveMaxLength(20);
        cfg.Properties<Modalidade>().HaveConversion<string>().HaveMaxLength(20);
        cfg.Properties<decimal>().HavePrecision(10, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasPostgresExtension("btree_gist");

        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Nome).HasMaxLength(120);
            e.Property(x => x.Slug).HasMaxLength(60);
            e.Property(x => x.Crp).HasMaxLength(20);
            e.Property(x => x.FusoHorario).HasMaxLength(60);
            e.Property(x => x.ChavePix).HasMaxLength(77);
            e.Property(x => x.CidadePix).HasMaxLength(15);
            e.Property(x => x.WhatsApp).HasMaxLength(20);
            e.Property(x => x.Bio).HasMaxLength(600);
            e.Property(x => x.OrigemCadastro).HasMaxLength(200);
        });

        b.Entity<ApplicationUser>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Nome).HasMaxLength(120);
        });

        b.Entity<Servico>(e =>
        {
            e.ToTable("servicos");
            e.Property(x => x.Nome).HasMaxLength(80);
        });

        b.Entity<Disponibilidade>().ToTable("disponibilidades");

        b.Entity<Bloqueio>(e =>
        {
            e.ToTable("bloqueios");
            e.Property(x => x.Motivo).HasMaxLength(120);
            e.HasIndex(x => new { x.TenantId, x.Inicio });
        });

        b.Entity<Paciente>(e =>
        {
            e.ToTable("pacientes");
            e.Property(x => x.Nome).HasMaxLength(120);
            e.Property(x => x.WhatsApp).HasMaxLength(20);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.Observacoes).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.WhatsApp });
        });

        b.Entity<Recorrencia>(e =>
        {
            e.ToTable("recorrencias");
            e.HasOne(x => x.Paciente).WithMany().HasForeignKey(x => x.PacienteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Servico).WithMany().HasForeignKey(x => x.ServicoId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.LinkVideo).HasMaxLength(300);
        });

        b.Entity<Agendamento>(e =>
        {
            e.ToTable("agendamentos");
            e.HasOne(x => x.Paciente).WithMany().HasForeignKey(x => x.PacienteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Servico).WithMany().HasForeignKey(x => x.ServicoId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Recorrencia).WithMany().HasForeignKey(x => x.RecorrenciaId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Cobranca).WithOne(x => x.Agendamento).HasForeignKey<Cobranca>(x => x.AgendamentoId);
            e.Property(x => x.LinkVideo).HasMaxLength(300);
            e.Property(x => x.TokenPublico).HasMaxLength(64);
            e.HasIndex(x => x.TokenPublico).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Inicio });
            e.HasIndex(x => new { x.Status, x.Inicio });
            // A restrição de exclusão (tstzrange) é criada na migration: dois agendamentos ativos
            // do mesmo psicólogo nunca se sobrepõem, mesmo com requisições simultâneas.
        });

        b.Entity<Mensagem>(e =>
        {
            e.ToTable("mensagens");
            e.HasOne(x => x.Agendamento).WithMany(x => x.Mensagens).HasForeignKey(x => x.AgendamentoId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Canal).HasMaxLength(20);
            e.Property(x => x.Template).HasMaxLength(60);
            e.Property(x => x.IdMeta).HasMaxLength(120);
            e.Property(x => x.Erro).HasMaxLength(500);
            e.Property(x => x.Custo).HasPrecision(10, 4);
            e.HasIndex(x => x.IdMeta);
        });

        b.Entity<Cobranca>(e =>
        {
            e.ToTable("cobrancas");
            e.HasIndex(x => x.AgendamentoId).IsUnique();
        });

        b.Entity<Assinatura>(e =>
        {
            e.ToTable("assinaturas");
            e.HasIndex(x => x.TenantId).IsUnique();
            e.HasIndex(x => x.IdAsaas);
            e.Property(x => x.IdAsaas).HasMaxLength(60);
            e.Property(x => x.IdClienteAsaas).HasMaxLength(60);
            e.Property(x => x.Plano).HasMaxLength(20);
            e.Property(x => x.UrlUltimaFatura).HasMaxLength(300);
        });

        b.Entity<Auditoria>(e =>
        {
            e.ToTable("auditorias");
            e.Property(x => x.Usuario).HasMaxLength(160);
            e.Property(x => x.Acao).HasMaxLength(60);
            e.Property(x => x.Entidade).HasMaxLength(60);
            e.Property(x => x.EntidadeId).HasMaxLength(60);
            e.HasIndex(x => new { x.TenantId, x.Data });
        });

        b.Entity<WebhookEvento>(e =>
        {
            e.ToTable("webhook_eventos");
            e.Property(x => x.Id).HasMaxLength(200);
            e.Property(x => x.Origem).HasMaxLength(20);
        });

        // Filtro global por TenantId em toda entidade de negócio.
        foreach (var tipo in b.Model.GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)))
        {
            var metodo = typeof(AppDbContext).GetMethod(nameof(AplicarFiltro),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            metodo.MakeGenericMethod(tipo.ClrType).Invoke(this, [b]);
        }
    }

    private void AplicarFiltro<T>(ModelBuilder b) where T : class, ITenantEntity
    {
        Expression<Func<T, bool>> filtro = e => e.TenantId == TenantAtual;
        b.Entity<T>().HasQueryFilter(filtro);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
                entry.Entity.TenantId = tenant.TenantIdObrigatorio;
            else if (entry.State is EntityState.Modified or EntityState.Deleted
                     && tenant.TenantId is { } atual && entry.Entity.TenantId != atual)
                throw new UnauthorizedAccessException("Tentativa de alterar dado de outro psicólogo.");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }
}
