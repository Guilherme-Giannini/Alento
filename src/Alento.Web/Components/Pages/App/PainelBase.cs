using Alento.Web.Components.Layout;
using Alento.Web.Modules.Contas;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Alento.Web.Components.Pages.App;

/// <summary>Base das telas do painel: acesso à conta atual e tratamento padrão de erros de regra de negócio.</summary>
public abstract class PainelBase : ComponentBase
{
    [CascadingParameter] public ContaAtual Conta { get; set; } = default!;
    [Inject] protected ISnackbar Snackbar { get; set; } = default!;
    [Inject] protected IDialogService Dialogos { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    protected async Task<bool> Executar(Func<Task> acao, string? sucesso = null)
    {
        try
        {
            await acao();
            if (sucesso is not null) Snackbar.Add(sucesso, Severity.Success);
            return true;
        }
        catch (RegraNegocioException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
        }
        catch (FluentValidation.ValidationException ex)
        {
            Snackbar.Add(string.Join(" ", ex.Errors.Select(e => e.ErrorMessage).Distinct()), Severity.Warning);
        }
        return false;
    }

    protected async Task Copiar(string texto, string mensagem = "Copiado!")
    {
        if (await JS.InvokeAsync<bool>("alento.copiar", texto)) Snackbar.Add(mensagem, Severity.Success);
    }

    protected static DialogOptions Opcoes(MaxWidth largura = MaxWidth.Small) =>
        new() { MaxWidth = largura, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true };
}
