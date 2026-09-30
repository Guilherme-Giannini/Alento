using Alento.Web.Data;
using FluentValidation;

namespace Alento.Web.Modules.Agenda;

public class ServicoValidator : AbstractValidator<Servico>
{
    public ServicoValidator()
    {
        RuleFor(x => x.Nome).NotEmpty().WithMessage("Dê um nome ao serviço.").MaximumLength(80);
        RuleFor(x => x.DuracaoMinutos).InclusiveBetween(10, 480).WithMessage("A duração deve ficar entre 10 e 480 minutos.");
        RuleFor(x => x.Valor).GreaterThanOrEqualTo(0).LessThan(100_000).WithMessage("Valor inválido.");
    }
}

public class DisponibilidadeValidator : AbstractValidator<Disponibilidade>
{
    public DisponibilidadeValidator()
    {
        RuleFor(x => x.DiaDaSemana).IsInEnum();
        RuleFor(x => x.Fim).GreaterThan(x => x.Inicio).WithMessage("O fim precisa ser depois do início.");
    }
}

public class BloqueioValidator : AbstractValidator<Bloqueio>
{
    public BloqueioValidator()
    {
        RuleFor(x => x.Fim).GreaterThan(x => x.Inicio).WithMessage("O fim do bloqueio precisa ser depois do início.");
        RuleFor(x => x.Motivo).MaximumLength(120);
        RuleFor(x => x).Must(x => (x.Fim - x.Inicio).TotalDays <= 366).WithMessage("Bloqueios de no máximo 1 ano.");
    }
}

public record NovoAgendamento
{
    public Guid PacienteId { get; init; }
    public Guid ServicoId { get; init; }
    public DateTime InicioUtc { get; init; }
    public string? LinkVideo { get; init; }
    public decimal? Valor { get; init; }
}

public class NovoAgendamentoValidator : AbstractValidator<NovoAgendamento>
{
    public NovoAgendamentoValidator()
    {
        RuleFor(x => x.PacienteId).NotEmpty().WithMessage("Escolha o paciente.");
        RuleFor(x => x.ServicoId).NotEmpty().WithMessage("Escolha o serviço.");
        RuleFor(x => x.InicioUtc).Must(d => d.Kind == DateTimeKind.Utc).WithMessage("Data em formato inválido.");
        RuleFor(x => x.LinkVideo).MaximumLength(300)
            .Must(l => string.IsNullOrWhiteSpace(l) || Uri.TryCreate(l, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
            .WithMessage("O link da videochamada precisa começar com https://");
        RuleFor(x => x.Valor).GreaterThanOrEqualTo(0).When(x => x.Valor.HasValue);
    }
}

public record NovaRecorrencia
{
    public Guid PacienteId { get; init; }
    public Guid ServicoId { get; init; }
    public DayOfWeek DiaDaSemana { get; init; }
    public TimeOnly Horario { get; init; }
    public DateOnly InicioEm { get; init; }
    public DateOnly? FimEm { get; init; }
    public string? LinkVideo { get; init; }
}

public class NovaRecorrenciaValidator : AbstractValidator<NovaRecorrencia>
{
    public NovaRecorrenciaValidator()
    {
        RuleFor(x => x.PacienteId).NotEmpty().WithMessage("Escolha o paciente.");
        RuleFor(x => x.ServicoId).NotEmpty().WithMessage("Escolha o serviço.");
        RuleFor(x => x.FimEm).GreaterThan(x => x.InicioEm).When(x => x.FimEm.HasValue).WithMessage("O fim precisa ser depois do início.");
        RuleFor(x => x.LinkVideo).MaximumLength(300);
    }
}

public record ReservaPublica
{
    public Guid ServicoId { get; init; }
    public DateTime InicioUtc { get; init; }
    public string Nome { get; init; } = "";
    public string WhatsApp { get; init; } = "";
    public string? Email { get; init; }
    public bool Consentimento { get; init; }
}

public class ReservaPublicaValidator : AbstractValidator<ReservaPublica>
{
    public ReservaPublicaValidator()
    {
        RuleFor(x => x.Nome).NotEmpty().WithMessage("Informe seu nome.").MaximumLength(120);
        RuleFor(x => x.WhatsApp).Must(Texto.WhatsAppValido).WithMessage("Informe um WhatsApp válido com DDD.");
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage("E-mail inválido.");
        RuleFor(x => x.Consentimento).Equal(true).WithMessage("É preciso aceitar receber o lembrete da sessão.");
    }
}
