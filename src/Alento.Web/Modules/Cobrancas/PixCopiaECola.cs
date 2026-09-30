using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Alento.Web.Modules.Cobrancas;

/// <summary>
/// Gera o BR Code estático (Pix copia-e-cola) com a chave do próprio psicólogo, conforme o
/// Manual de Padrões para Iniciação do Pix do Banco Central (EMV-MPM + CRC16-CCITT).
/// </summary>
public static partial class PixCopiaECola
{
    public static string Gerar(string chave, string nomeRecebedor, string cidade, decimal? valor, string? txid = null, string? descricao = null)
    {
        if (string.IsNullOrWhiteSpace(chave)) throw new ArgumentException("Chave Pix obrigatória.", nameof(chave));

        var conta = Campo("00", "br.gov.bcb.pix") + Campo("01", NormalizarChave(chave));
        if (!string.IsNullOrWhiteSpace(descricao)) conta += Campo("02", Limpar(descricao, 40));

        var sb = new StringBuilder()
            .Append(Campo("00", "01"))
            .Append(Campo("26", conta))
            .Append(Campo("52", "0000"))
            .Append(Campo("53", "986"));
        if (valor is > 0) sb.Append(Campo("54", valor.Value.ToString("0.00", CultureInfo.InvariantCulture)));
        sb.Append(Campo("58", "BR"))
          .Append(Campo("59", Limpar(nomeRecebedor, 25, maiusculo: true)))
          .Append(Campo("60", Limpar(string.IsNullOrWhiteSpace(cidade) ? "SAO PAULO" : cidade, 15, maiusculo: true)))
          .Append(Campo("62", Campo("05", TxId(txid))))
          .Append("6304");

        var payload = sb.ToString();
        return payload + Crc16(payload).ToString("X4");
    }

    public static string NormalizarChave(string chave)
    {
        chave = chave.Trim();
        if (chave.Contains('@')) return chave.ToLowerInvariant();
        if (chave.StartsWith('+')) return "+" + new string(chave.Where(char.IsDigit).ToArray());
        if (CpfCnpjFormatado().IsMatch(chave)) return new string(chave.Where(char.IsDigit).ToArray());
        return chave;
    }

    public static ushort Crc16(string dados)
    {
        ushort crc = 0xFFFF;
        foreach (var b in Encoding.UTF8.GetBytes(dados))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    private static string Campo(string id, string valor) => $"{id}{Encoding.UTF8.GetByteCount(valor):00}{valor}";

    private static string TxId(string? txid)
    {
        var limpo = new string((txid ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        return limpo.Length == 0 ? "***" : limpo[..Math.Min(25, limpo.Length)];
    }

    private static string Limpar(string s, int max, bool maiusculo = false)
    {
        var t = new string(Texto.RemoverAcentos(s).Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
        if (maiusculo) t = t.ToUpperInvariant();
        return t.Length > max ? t[..max].Trim() : t;
    }

    [GeneratedRegex(@"^\d{3}\.\d{3}\.\d{3}-\d{2}$|^\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}$")]
    private static partial Regex CpfCnpjFormatado();
}
