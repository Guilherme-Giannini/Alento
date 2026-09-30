using Microsoft.AspNetCore.Identity;

namespace Alento.Web.Infra;

public class IdentityErrosPt : IdentityErrorDescriber
{
    public override IdentityError DuplicateEmail(string email) => new() { Code = nameof(DuplicateEmail), Description = $"Já existe uma conta com o e-mail {email}." };
    public override IdentityError DuplicateUserName(string userName) => new() { Code = nameof(DuplicateUserName), Description = $"Já existe uma conta com o e-mail {userName}." };
    public override IdentityError InvalidEmail(string? email) => new() { Code = nameof(InvalidEmail), Description = "E-mail inválido." };
    public override IdentityError InvalidUserName(string? userName) => new() { Code = nameof(InvalidUserName), Description = "E-mail inválido." };
    public override IdentityError PasswordTooShort(int length) => new() { Code = nameof(PasswordTooShort), Description = $"A senha precisa ter pelo menos {length} caracteres." };
    public override IdentityError PasswordRequiresDigit() => new() { Code = nameof(PasswordRequiresDigit), Description = "A senha precisa ter pelo menos um número." };
    public override IdentityError PasswordRequiresLower() => new() { Code = nameof(PasswordRequiresLower), Description = "A senha precisa ter pelo menos uma letra minúscula." };
    public override IdentityError PasswordRequiresUpper() => new() { Code = nameof(PasswordRequiresUpper), Description = "A senha precisa ter pelo menos uma letra maiúscula." };
    public override IdentityError PasswordRequiresNonAlphanumeric() => new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "A senha precisa ter pelo menos um símbolo." };
    public override IdentityError PasswordMismatch() => new() { Code = nameof(PasswordMismatch), Description = "Senha incorreta." };
    public override IdentityError InvalidToken() => new() { Code = nameof(InvalidToken), Description = "Link inválido ou expirado." };
    public override IdentityError DefaultError() => new() { Code = nameof(DefaultError), Description = "Ocorreu um erro. Tente novamente." };
}
