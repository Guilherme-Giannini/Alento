using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Alento.Web;

public static class Seguranca
{
    public static string NovoToken(int bytes = 24) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool CompararConstante(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Datas ficam em UTC no banco e são convertidas pelo fuso do psicólogo só na borda (tela e mensagens).</summary>
public static class Fuso
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static TimeZoneInfo Obter(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "America/Sao_Paulo" : id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
    }

    public static DateTime ParaLocal(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

    public static DateTime ParaUtc(DateTime local, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);

    public static DateTime ParaUtc(DateOnly dia, TimeOnly hora, TimeZoneInfo tz) =>
        ParaUtc(dia.ToDateTime(hora), tz);

    public static DateOnly Hoje(TimeZoneInfo tz) => DateOnly.FromDateTime(ParaLocal(DateTime.UtcNow, tz));

    public static string DiaSemana(DayOfWeek d) => PtBr.DateTimeFormat.GetDayName(d);

    public static string DiaSemanaCurto(DayOfWeek d) =>
        PtBr.DateTimeFormat.GetAbbreviatedDayName(d).TrimEnd('.');
}

public static partial class Texto
{
    /// <summary>Normaliza para E.164 sem o "+" (formato da API do WhatsApp). Assume Brasil quando vier sem DDI.</summary>
    public static string NormalizarWhatsApp(string? numero)
    {
        var digitos = new string((numero ?? "").Where(char.IsDigit).ToArray());
        if (digitos.Length is 10 or 11) digitos = "55" + digitos;
        return digitos;
    }

    public static bool WhatsAppValido(string? numero)
    {
        var n = NormalizarWhatsApp(numero);
        return n.Length is >= 12 and <= 13 && n.StartsWith("55") || n.Length is >= 10 and <= 15 && !n.StartsWith("55");
    }

    public static string FormatarWhatsApp(string? numero)
    {
        var n = NormalizarWhatsApp(numero);
        if (n.StartsWith("55") && n.Length == 13) return $"({n[2..4]}) {n[4..9]}-{n[9..]}";
        if (n.StartsWith("55") && n.Length == 12) return $"({n[2..4]}) {n[4..8]}-{n[8..]}";
        return numero ?? "";
    }

    public static string RemoverAcentos(string s)
    {
        var normalizado = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalizado)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string Slug(string s)
    {
        var slug = NaoAlfanumerico().Replace(RemoverAcentos(s).ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 50 ? slug[..50].TrimEnd('-') : slug;
    }

    public static string Moeda(decimal v) => v.ToString("C", Fuso.PtBr);

    public static string PrimeiroNome(string? nome) => (nome ?? "").Trim().Split(' ', 2)[0];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NaoAlfanumerico();
}
