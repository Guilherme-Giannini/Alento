using Microsoft.AspNetCore.Identity;

namespace Alento.Web.Data;

/// <summary>Toda entidade de negócio pertence a um psicólogo (Tenant) e é filtrada automaticamente.</summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}

public enum StatusAssinatura
{
    Teste,
    Ativa,
    Inadimplente,
    Cancelada
}

public enum StatusAgendamento
{
    Agendado,
    Confirmado,
    Realizado,
    Faltou,
    Cancelado
}

public enum Modalidade
{
    Presencial,
    Online
}

public enum StatusMensagem
{
    Pendente,
    Enviada,
    Entregue,
    Lida,
    Falhou
}

public enum StatusCobranca
{
    Pendente,
    Paga,
    Cancelada
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Crp { get; set; }
    public string FusoHorario { get; set; } = "America/Sao_Paulo";
    public string? ChavePix { get; set; }
    public string? CidadePix { get; set; }
    public string? WhatsApp { get; set; }
    public string? Bio { get; set; }
    public int IntervaloEntreSessoesMinutos { get; set; } = 10;
    public int AntecedenciaMinimaHoras { get; set; } = 12;
    public int JanelaAgendamentoDias { get; set; } = 30;
    public StatusAssinatura StatusAssinatura { get; set; } = StatusAssinatura.Teste;
    public DateTime TesteTerminaEm { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? DpaAceitoEm { get; set; }
    public string? OrigemCadastro { get; set; }
}

public class ApplicationUser : IdentityUser
{
    public Guid TenantId { get; set; }

    [PersonalData]
    public string Nome { get; set; } = "";
}

public class Servico : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Nome { get; set; } = "";
    public int DuracaoMinutos { get; set; } = 50;
    public decimal Valor { get; set; }
    public Modalidade Modalidade { get; set; } = Modalidade.Presencial;
    public bool Ativo { get; set; } = true;
    public bool VisivelNoLinkPublico { get; set; } = true;
}

public class Disponibilidade : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DayOfWeek DiaDaSemana { get; set; }
    public TimeOnly Inicio { get; set; }
    public TimeOnly Fim { get; set; }
}

public class Bloqueio : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateTime Inicio { get; set; }
    public DateTime Fim { get; set; }
    public string Motivo { get; set; } = "";
}

public class Paciente : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Nome { get; set; } = "";
    public string WhatsApp { get; set; } = "";
    public string? Email { get; set; }
    public bool ConsentimentoMensagens { get; set; }
    public DateTime? ConsentimentoEm { get; set; }
    public decimal? ValorSessaoPersonalizado { get; set; }
    public string? Observacoes { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public bool Ativo { get; set; } = true;
}

public class Recorrencia : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PacienteId { get; set; }
    public Paciente? Paciente { get; set; }
    public Guid ServicoId { get; set; }
    public Servico? Servico { get; set; }
    public DayOfWeek DiaDaSemana { get; set; }
    public TimeOnly Horario { get; set; }
    public DateOnly InicioEm { get; set; }
    public DateOnly? FimEm { get; set; }
    public string? LinkVideo { get; set; }
    public DateOnly? GeradoAte { get; set; }
}

public class Agendamento : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PacienteId { get; set; }
    public Paciente? Paciente { get; set; }
    public Guid ServicoId { get; set; }
    public Servico? Servico { get; set; }
    public Guid? RecorrenciaId { get; set; }
    public Recorrencia? Recorrencia { get; set; }
    public DateTime Inicio { get; set; }
    public DateTime Fim { get; set; }
    public StatusAgendamento Status { get; set; } = StatusAgendamento.Agendado;
    public string? LinkVideo { get; set; }
    public decimal Valor { get; set; }
    /// <summary>Token aleatório usado nos links que o paciente recebe (confirmar/remarcar/cancelar), sem login.</summary>
    public string TokenPublico { get; set; } = Seguranca.NovoToken();
    public bool CriadoPeloPaciente { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmadoEm { get; set; }
    public List<Mensagem> Mensagens { get; set; } = [];
    public Cobranca? Cobranca { get; set; }
}

public class Mensagem : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? AgendamentoId { get; set; }
    public Agendamento? Agendamento { get; set; }
    public string Canal { get; set; } = "whatsapp";
    public string Template { get; set; } = "";
    public StatusMensagem Status { get; set; } = StatusMensagem.Pendente;
    public string? IdMeta { get; set; }
    public decimal Custo { get; set; }
    public string? Erro { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? AtualizadoEm { get; set; }
}

public class Cobranca : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AgendamentoId { get; set; }
    public Agendamento? Agendamento { get; set; }
    public decimal Valor { get; set; }
    public StatusCobranca Status { get; set; } = StatusCobranca.Pendente;
    public DateTime? PagoEm { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}

public class Assinatura : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string? IdClienteAsaas { get; set; }
    public string? IdAsaas { get; set; }
    public string Plano { get; set; } = "mensal";
    public decimal Valor { get; set; }
    public DateTime? ProximoVencimento { get; set; }
    public StatusAssinatura Status { get; set; } = StatusAssinatura.Teste;
    public string? UrlUltimaFatura { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? AtualizadoEm { get; set; }
}

public class Auditoria : ITenantEntity
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string Usuario { get; set; } = "";
    public string Acao { get; set; } = "";
    public string Entidade { get; set; } = "";
    public string? EntidadeId { get; set; }
    public DateTime Data { get; set; } = DateTime.UtcNow;
}

/// <summary>Eventos de webhook já processados (idempotência do Asaas e da Meta).</summary>
public class WebhookEvento
{
    public string Id { get; set; } = "";
    public string Origem { get; set; } = "";
    public DateTime RecebidoEm { get; set; } = DateTime.UtcNow;
}
