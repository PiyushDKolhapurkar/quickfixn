using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace BirthdayBot;

/// <summary>A delivery failed; the bot falls back to a reminder.</summary>
public sealed class DeliveryException : Exception
{
    public DeliveryException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Sends a wish automatically. Implemented by <see cref="WhatsAppCloudChannel"/>.</summary>
public interface IAutoSender
{
    string Send(Contact contact, string text);
}

public static class ChatLinks
{
    /// <summary>A link that opens a chat with the contact on the platform, or null if no handle is known.</summary>
    public static string? For(Contact contact, string platform, string text)
    {
        switch (platform)
        {
            case "whatsapp" when contact.WhatsApp.Length > 0:
                return $"https://wa.me/{contact.WhatsApp.TrimStart('+')}?text={Uri.EscapeDataString(text)}";
            case "instagram" when contact.Instagram.Length > 0:
                return $"https://ig.me/m/{Uri.EscapeDataString(contact.Instagram)}";
            case "facebook" when contact.Facebook.Length > 0:
                return $"https://m.me/{Uri.EscapeDataString(contact.Facebook)}";
            default:
                return null;
        }
    }
}

/// <summary>
/// Fully automatic sending through Meta's official WhatsApp Cloud API. Needs a WhatsApp
/// Business phone number and an approved message template (messages a business starts
/// must use a template).
/// </summary>
public sealed class WhatsAppCloudChannel : IAutoSender
{
    private readonly HttpClient _http;
    private readonly WhatsAppCloudSettings _settings;
    private readonly string _token;

    public WhatsAppCloudChannel(WhatsAppCloudSettings settings, string accessToken, HttpClient? http = null)
    {
        _settings = settings;
        _token = accessToken;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Send(Contact contact, string text)
    {
        if (contact.WhatsApp.Length == 0)
            throw new DeliveryException($"{contact.Name} has no WhatsApp number");

        var to = contact.WhatsApp.TrimStart('+');
        object payload = _settings.TemplateName.Length > 0
            ? new
            {
                messaging_product = "whatsapp",
                to,
                type = "template",
                template = new
                {
                    name = _settings.TemplateName,
                    language = new { code = _settings.TemplateLanguage },
                    components = new[]
                    {
                        new { type = "body", parameters = new[] { new { type = "text", text = contact.FirstName } } },
                    },
                },
            }
            // Free-form text only arrives if the person messaged you in the last 24 hours.
            : new { messaging_product = "whatsapp", to, type = "text", text = new { body = text } };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{_settings.ApiVersion}/{_settings.PhoneNumberId}/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        HttpResponseMessage resp;
        string body;
        try
        {
            resp = _http.Send(req);
            body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new DeliveryException($"WhatsApp API unreachable: {e.Message}", e);
        }
        if (!resp.IsSuccessStatusCode)
            throw new DeliveryException($"WhatsApp API error {(int)resp.StatusCode}: {body}");

        string id = "?";
        try
        {
            using var doc = JsonDocument.Parse(body);
            id = doc.RootElement.GetProperty("messages")[0].GetProperty("id").GetString() ?? "?";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { }
        return $"sent via WhatsApp Cloud API (id {id})";
    }
}

/// <summary>
/// For everything that can't be automated within the platforms' rules (personal
/// Messenger, Instagram DMs, personal WhatsApp): sends you a digest with one-tap
/// links that open the chat (with the text already typed, for WhatsApp).
/// </summary>
public sealed class ReminderChannel
{
    private readonly ReminderSettings _settings;
    private readonly string _smtpPassword;
    private readonly TextWriter _out;
    private readonly List<string> _items = new();

    public ReminderChannel(ReminderSettings? settings = null, string smtpPassword = "", TextWriter? output = null)
    {
        _settings = settings ?? new ReminderSettings();
        _smtpPassword = smtpPassword;
        _out = output ?? Console.Out;
    }

    public string Add(Contact contact, string text, IReadOnlyList<string> platforms)
    {
        var links = platforms
            .Select(p => (p, link: ChatLinks.For(contact, p, text)))
            .Where(x => x.link != null)
            .Select(x => $"  {x.p}: {x.link}")
            .ToList();
        var entry = $"* {contact.Name}\n  message: {text}\n" +
                    (links.Count > 0 ? string.Join('\n', links) : "  (no chat handle on file - add one in contacts.csv)");
        _items.Add(entry);
        return links.Count > 0 ? $"reminder queued with {links.Count} link(s)" : "reminder queued";
    }

    /// <summary>Prints the digest (and emails it if SMTP is configured). Returns the text sent.</summary>
    public string Flush(string title)
    {
        if (_items.Count == 0)
            return "";
        var body = title + "\n\n" + string.Join("\n\n", _items) + "\n";
        _out.WriteLine(body);

        if (_settings.EmailTo.Length > 0 && _settings.SmtpHost.Length > 0)
        {
            using var smtp = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort) { EnableSsl = true };
            if (_settings.SmtpUser.Length > 0)
                smtp.Credentials = new System.Net.NetworkCredential(_settings.SmtpUser, _smtpPassword);
            var from = _settings.EmailFrom.Length > 0 ? _settings.EmailFrom : _settings.SmtpUser;
            using var msg = new MailMessage(from, _settings.EmailTo, title, body) { BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 };
            smtp.Send(msg);
        }
        _items.Clear();
        return body;
    }
}
