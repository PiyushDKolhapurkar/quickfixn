using System.Text.Json;

namespace BirthdayBot;

public sealed class WhatsAppCloudSettings
{
    public bool Enabled { get; set; }
    public string PhoneNumberId { get; set; } = "";
    /// <summary>Prefer the WHATSAPP_TOKEN environment variable over putting the token in the file.</summary>
    public string AccessToken { get; set; } = "";
    /// <summary>Approved template name, e.g. "birthday_wish". Blank sends plain text.</summary>
    public string TemplateName { get; set; } = "";
    public string TemplateLanguage { get; set; } = "en";
    public string ApiVersion { get; set; } = "v21.0";
}

public sealed class ReminderSettings
{
    public string EmailTo { get; set; } = "";
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    /// <summary>Prefer the SMTP_PASSWORD environment variable.</summary>
    public string SmtpPassword { get; set; } = "";
    public string EmailFrom { get; set; } = "";
}

public sealed class BotConfig
{
    public static readonly string[] DefaultMessages =
    {
        "Happy birthday, {first_name}! 🎉 Wishing you a fantastic year ahead.",
        "Happy birthday {first_name}! 🎂 Hope your day is as awesome as you are.",
        "Many happy returns of the day, {first_name}! 🥳 Have a wonderful celebration.",
    };

    public string ContactsFile { get; set; } = "data/contacts.csv";
    public string StateFile { get; set; } = "data/sent.json";
    /// <summary>Used to turn local numbers like 09876 543210 into +919876543210.</summary>
    public string DefaultCountryCode { get; set; } = "";
    public List<string> ChannelOrder { get; set; } = new() { "whatsapp", "instagram", "facebook" };
    /// <summary>One is picked at random per person. Placeholders: {first_name} {name} {age}.</summary>
    public List<string> Messages { get; set; } = new(DefaultMessages);
    public WhatsAppCloudSettings WhatsAppCloud { get; set; } = new();
    public ReminderSettings Reminders { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads config.json; relative paths in it are resolved against the file's folder.</summary>
    public static BotConfig Load(string? path)
    {
        var cfg = new BotConfig();
        var baseDir = Directory.GetCurrentDirectory();
        if (path != null && File.Exists(path))
        {
            cfg = JsonSerializer.Deserialize<BotConfig>(File.ReadAllText(path), JsonOptions) ?? new BotConfig();
            baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        }
        cfg.ContactsFile = Path.GetFullPath(cfg.ContactsFile, baseDir);
        cfg.StateFile = Path.GetFullPath(cfg.StateFile, baseDir);
        if (cfg.Messages.Count == 0)
            cfg.Messages = new(DefaultMessages);
        if (cfg.ChannelOrder.Count == 0)
            cfg.ChannelOrder = new() { "whatsapp", "instagram", "facebook" };
        return cfg;
    }

    public IAutoSender? CreateWhatsAppSender()
    {
        var token = Env("WHATSAPP_TOKEN") ?? WhatsAppCloud.AccessToken;
        var phoneId = Env("WHATSAPP_PHONE_NUMBER_ID") ?? WhatsAppCloud.PhoneNumberId;
        if (!WhatsAppCloud.Enabled || token.Length == 0 || phoneId.Length == 0)
            return null;
        WhatsAppCloud.PhoneNumberId = phoneId;
        return new WhatsAppCloudChannel(WhatsAppCloud, token);
    }

    public ReminderChannel CreateReminderChannel() =>
        new(Reminders, Env("SMTP_PASSWORD") ?? Reminders.SmtpPassword);

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
