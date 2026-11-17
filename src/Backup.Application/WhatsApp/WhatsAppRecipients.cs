namespace Backup.Application.WhatsApp;

/// <summary>Convierte lo que escribe el usuario (número o ID de grupo) en el chatId que espera WAHA.</summary>
public static class WhatsAppRecipients
{
    /// <summary>Cómo se escribe un grupo en la lista de destinatarios: "Nombre &lt;id@g.us&gt;".</summary>
    public static string Format(WhatsAppChat chat) => $"{chat.Name.Replace("<", "(", StringComparison.Ordinal).Replace(">", ")", StringComparison.Ordinal).Replace(",", " ", StringComparison.Ordinal)} <{chat.Id}>";

    /// <summary>
    /// "+506 7074-5530" → "50670745530@c.us"; "Nombre &lt;id@g.us&gt;" → "id@g.us"; los IDs con "@" (grupos <c>…@g.us</c>, contactos <c>…@c.us</c>) se
    /// dejan tal cual. Null si no es un número válido (8 a 15 dígitos, con código de país).
    /// </summary>
    public static string? ToChatId(string value)
    {
        var text = value.Trim();

        // "TI Respaldos <120363…@g.us>": nombre legible y el ID entre < >.
        var open = text.LastIndexOf('<');
        if (open >= 0 && text.EndsWith('>'))
        {
            text = text[(open + 1)..^1].Trim();
        }

        if (text.Contains('@', StringComparison.Ordinal))
        {
            var at = text.IndexOf('@', StringComparison.Ordinal);
            return at > 0 && text[(at + 1)..] is "g.us" or "c.us" or "lid" or "newsletter" ? text : null;
        }

        var digits = new string([.. text.Where(char.IsAsciiDigit)]);
        var onlyNumberChars = text.All(c => char.IsAsciiDigit(c) || c is '+' or ' ' or '-' or '(' or ')' or '.');
        return onlyNumberChars && digits.Length is >= 8 and <= 15 ? $"{digits}@c.us" : null;
    }
}
