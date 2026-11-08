using System.Text;
using System.Text.Json;

namespace BirthdayBot;

/// <summary>Contacts live in a plain CSV file you can open and edit in any spreadsheet.</summary>
public static class ContactStore
{
    private static readonly string[] Fields =
        { "name", "birthday", "whatsapp", "instagram", "facebook", "channel", "message", "enabled", "sources" };

    private static readonly HashSet<string> FalseValues = new(StringComparer.OrdinalIgnoreCase) { "no", "false", "0", "n" };

    public static List<Contact> Load(string path)
    {
        if (!File.Exists(path))
            return new();
        var contacts = new List<Contact>();
        foreach (var row in Csv.ParseWithHeader(File.ReadAllText(path, Encoding.UTF8)))
        {
            string Get(string key) => row.TryGetValue(key, out var v) ? v.Trim() : "";
            var name = Get("name");
            if (name.Length == 0)
                continue;
            var enabled = Get("enabled");
            contacts.Add(new Contact(name)
            {
                Birthday = Birthday.Parse(Get("birthday")),
                WhatsApp = Get("whatsapp"),
                Instagram = Get("instagram").TrimStart('@'),
                Facebook = Get("facebook"),
                Channel = Get("channel").ToLowerInvariant(),
                Message = Get("message"),
                Enabled = enabled.Length == 0 || !FalseValues.Contains(enabled),
                Sources = new SortedSet<string>(Get("sources").Split(';', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal),
            });
        }
        return contacts;
    }

    public static void Save(string path, IEnumerable<Contact> contacts)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null)
            Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Fields)).Append('\n');
        foreach (var c in contacts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            sb.Append(Csv.FormatRow(new[]
            {
                c.Name, c.Birthday?.ToString() ?? "", c.WhatsApp, c.Instagram, c.Facebook,
                c.Channel, c.Message, c.Enabled ? "yes" : "no", string.Join(';', c.Sources),
            })).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Merges imported contacts into the existing list, matching by name.
    /// Existing non-empty values are never overwritten, so manual edits are safe.</summary>
    public static (List<Contact> Merged, int Added, int Updated) Merge(List<Contact> existing, IEnumerable<Contact> incoming)
    {
        var byKey = new Dictionary<string, Contact>();
        var order = new List<Contact>();
        foreach (var c in existing)
            if (byKey.TryAdd(c.Key, c))
                order.Add(c);

        int added = 0, updated = 0;
        foreach (var n in incoming)
        {
            if (!byKey.TryGetValue(n.Key, out var cur))
            {
                byKey[n.Key] = n;
                order.Add(n);
                added++;
                continue;
            }
            bool changed = false;
            if (cur.Birthday is null && n.Birthday is not null) { cur.Birthday = n.Birthday; changed = true; }
            if (cur.WhatsApp.Length == 0 && n.WhatsApp.Length > 0) { cur.WhatsApp = n.WhatsApp; changed = true; }
            if (cur.Instagram.Length == 0 && n.Instagram.Length > 0) { cur.Instagram = n.Instagram; changed = true; }
            if (cur.Facebook.Length == 0 && n.Facebook.Length > 0) { cur.Facebook = n.Facebook; changed = true; }
            if (!n.Sources.IsSubsetOf(cur.Sources)) { cur.Sources.UnionWith(n.Sources); changed = true; }
            if (changed) updated++;
        }
        return (order, added, updated);
    }
}

/// <summary>Remembers who was already wished each year so re-runs never double-send.</summary>
public sealed class SentLog
{
    private readonly string _path;
    private readonly Dictionary<string, List<string>> _data;

    public SentLog(string path)
    {
        _path = path;
        _data = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path)) ?? new()
            : new();
    }

    public bool AlreadySent(Contact contact, int year) =>
        _data.TryGetValue(year.ToString(), out var keys) && keys.Contains(contact.Key);

    public void MarkSent(Contact contact, int year)
    {
        var y = year.ToString();
        if (!_data.TryGetValue(y, out var keys))
            _data[y] = keys = new List<string>();
        if (!keys.Contains(contact.Key))
            keys.Add(contact.Key);
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (dir != null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
