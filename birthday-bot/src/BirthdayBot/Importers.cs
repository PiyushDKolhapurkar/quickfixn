using System.Text;
using System.Text.Json;

namespace BirthdayBot;

/// <summary>
/// Imports friends/contacts from the data exports each platform officially provides.
///
/// Facebook, Instagram and WhatsApp have no API that lets a personal account read
/// its friend list with birthdays, so the bot works from exports:
/// <list type="bullet">
/// <item>Phone / WhatsApp contacts: a .vcf export (vCards carry BDAY). WhatsApp uses your
///   phone's address book, so this is your WhatsApp list.</item>
/// <item>Google Contacts: contacts.google.com &gt; Export &gt; Google CSV.</item>
/// <item>Facebook: "Download your information" (JSON) &gt; connections/friends/your_friends.json.</item>
/// <item>Instagram: "Download your information" (JSON) &gt;
///   connections/followers_and_following/following.json (and/or followers_1.json).</item>
/// </list>
/// Facebook and Instagram exports have names/usernames but no birthdays.
/// </summary>
public static class Importers
{
    public static readonly string[] Sources = { "facebook", "google", "instagram", "vcard" };

    public static List<Contact> Import(string source, string path, string defaultCountryCode) => source switch
    {
        "vcard" => ImportVCard(path, defaultCountryCode),
        "google" => ImportGoogleCsv(path, defaultCountryCode),
        "facebook" => ImportFacebook(path),
        "instagram" => ImportInstagram(path),
        _ => throw new ArgumentException($"Unknown source '{source}'. Use one of: {string.Join(", ", Sources)}"),
    };

    /// <summary>Meta's JSON exports encode UTF-8 bytes as latin-1 escapes; undo that.</summary>
    internal static string FixMojibake(string text)
    {
        if (text.Any(c => c > 0xFF))
            return text;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Encoding.Latin1.GetBytes(text));
        }
        catch (DecoderFallbackException)
        {
            return text;
        }
    }

    /// <summary>Returns a phone number as "+&lt;digits&gt;", or "" if it doesn't look valid.</summary>
    public static string NormalizePhone(string raw, string defaultCountryCode = "")
    {
        raw = raw.Trim();
        if (raw.Length == 0)
            return "";
        bool international = raw.StartsWith('+') || raw.StartsWith("00");
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (raw.StartsWith("00"))
            digits = digits[2..];
        if (!international)
        {
            var cc = defaultCountryCode.TrimStart('+');
            if (cc.Length == 0)
                return digits.Length > 10 ? "+" + digits : "";
            digits = cc + digits.TrimStart('0');
        }
        return digits.Length is >= 8 and <= 15 ? "+" + digits : "";
    }

    // ------------------------------------------------------------ vCard (.vcf)

    private static IEnumerable<string> Unfold(IEnumerable<string> lines)
    {
        string? buf = null;
        foreach (var line in lines)
        {
            if (buf != null && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                buf += line[1..];
                continue;
            }
            if (buf != null)
                yield return buf;
            buf = line;
        }
        if (buf != null)
            yield return buf;
    }

    public static List<Contact> ImportVCard(string path, string defaultCountryCode = "")
    {
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        var contacts = new List<Contact>();
        var card = new Dictionary<string, List<string>>();

        foreach (var line in Unfold(lines))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var head = line[..colon];
            var value = line[(colon + 1)..];
            var prop = head.Split(';')[0].Split('.').Last().ToUpperInvariant();

            if (prop == "BEGIN")
            {
                card = new();
            }
            else if (prop == "END")
            {
                var name = First(card, "FN").Trim();
                if (name.Length == 0 && card.ContainsKey("N"))
                {
                    var parts = First(card, "N").Split(';');
                    name = string.Join(' ', new[] { parts.ElementAtOrDefault(1), parts[0] }.Where(p => !string.IsNullOrEmpty(p))).Trim();
                }
                if (name.Length == 0)
                    continue;
                var phone = (card.GetValueOrDefault("TEL") ?? new())
                    .Select(p => NormalizePhone(p, defaultCountryCode))
                    .FirstOrDefault(p => p.Length > 0) ?? "";
                contacts.Add(new Contact(name)
                {
                    Birthday = Birthday.Parse(First(card, "BDAY")),
                    WhatsApp = phone,
                    Sources = { "phone" },
                });
            }
            else
            {
                if (!card.TryGetValue(prop, out var list))
                    card[prop] = list = new();
                list.Add(value.Replace("\\,", ",").Replace("\\;", ";"));
            }
        }
        return contacts;
    }

    private static string First(Dictionary<string, List<string>> card, string key) =>
        card.TryGetValue(key, out var v) && v.Count > 0 ? v[0] : "";

    // -------------------------------------------------------- Google Contacts

    public static List<Contact> ImportGoogleCsv(string path, string defaultCountryCode = "")
    {
        var contacts = new List<Contact>();
        foreach (var row in Csv.ParseWithHeader(File.ReadAllText(path)))
        {
            string Get(string key) => row.TryGetValue(key, out var v) ? v.Trim() : "";
            var name = Get("Name");
            if (name.Length == 0)
            {
                var first = Get("First Name").Length > 0 ? Get("First Name") : Get("Given Name");
                var last = Get("Last Name").Length > 0 ? Get("Last Name") : Get("Family Name");
                name = $"{first} {last}".Trim();
            }
            if (name.Length == 0)
                continue;
            var phone = row
                .Where(kv => kv.Key.Contains("Phone") && kv.Key.Contains("Value") && kv.Value.Length > 0)
                .Select(kv => NormalizePhone(kv.Value.Split(":::")[0], defaultCountryCode))
                .FirstOrDefault(p => p.Length > 0) ?? "";
            contacts.Add(new Contact(name)
            {
                Birthday = Birthday.Parse(Get("Birthday")),
                WhatsApp = phone,
                Sources = { "google" },
            });
        }
        return contacts;
    }

    // ---------------------------------------------------------------- Facebook

    public static List<Contact> ImportFacebook(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        JsonElement entries = default;
        if (root.ValueKind == JsonValueKind.Array)
            entries = root;
        else if (root.ValueKind == JsonValueKind.Object &&
                 (root.TryGetProperty("friends_v2", out entries) || root.TryGetProperty("friends", out entries)))
        { }

        var contacts = new List<Contact>();
        if (entries.ValueKind != JsonValueKind.Array)
            return contacts;
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                contacts.Add(new Contact(FixMojibake(name).Trim()) { Sources = { "facebook" } });
        }
        return contacts;
    }

    // --------------------------------------------------------------- Instagram

    public static List<Contact> ImportInstagram(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        JsonElement entries = default;
        if (root.ValueKind == JsonValueKind.Array)
            entries = root;
        else if (root.ValueKind == JsonValueKind.Object)
            entries = root.EnumerateObject().FirstOrDefault(p => p.Name.StartsWith("relationships_")).Value;

        var contacts = new List<Contact>();
        if (entries.ValueKind != JsonValueKind.Array)
            return contacts;
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
                continue;
            JsonElement item = default;
            if (e.TryGetProperty("string_list_data", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                item = list[0];

            var username = Str(item, "value");
            if (username.Length == 0)
                username = Str(e, "title");
            if (username.Length == 0)
                username = Str(item, "href").TrimEnd('/').Split('/').Last();
            username = FixMojibake(username).Trim().TrimStart('@');
            if (username.Length > 0)
                contacts.Add(new Contact(username) { Instagram = username, Sources = { "instagram" } });
        }
        return contacts;
    }

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
