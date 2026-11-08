namespace BirthdayBot;

/// <summary>Works out who has a birthday today and sends the wishes.</summary>
public sealed class Bot
{
    private readonly BotConfig _config;
    private readonly IAutoSender? _whatsApp;
    private readonly ReminderChannel _reminders;

    public Bot(BotConfig config, IAutoSender? whatsApp, ReminderChannel reminders)
    {
        _config = config;
        _whatsApp = whatsApp;
        _reminders = reminders;
    }

    public static Bot FromConfig(BotConfig config) =>
        new(config, config.CreateWhatsAppSender(), config.CreateReminderChannel());

    public static string ComposeMessage(Contact contact, DateOnly today, IReadOnlyList<string> templates, Random rng)
    {
        var template = contact.Message.Length > 0 ? contact.Message : templates[rng.Next(templates.Count)];
        var age = contact.Birthday?.AgeOn(today);
        return template
            .Replace("{first_name}", contact.FirstName)
            .Replace("{name}", contact.Name)
            .Replace("{age}", age?.ToString() ?? "");
    }

    /// <summary>Platforms this contact can be reached on, preferred channel first.</summary>
    public static List<string> PlatformsFor(Contact contact, IEnumerable<string> order)
    {
        var available = order.Where(p => contact.HandleFor(p).Length > 0).ToList();
        if (available.Remove(contact.Channel))
            available.Insert(0, contact.Channel);
        return available;
    }

    public static IEnumerable<Contact> BirthdaysOn(IEnumerable<Contact> contacts, DateOnly today) =>
        contacts.Where(c => c.Enabled && c.Birthday is { } b && b.FallsOn(today));

    /// <summary>Wishes everyone whose birthday is <paramref name="today"/>. Returns a log of what happened.</summary>
    public List<string> Run(IEnumerable<Contact> contacts, DateOnly today, bool dryRun = false)
    {
        var log = new List<string>();
        var sent = new SentLog(_config.StateFile);
        var rng = new Random(today.DayNumber);

        foreach (var contact in BirthdaysOn(contacts, today))
        {
            if (sent.AlreadySent(contact, today.Year))
            {
                log.Add($"{contact.Name}: already wished this year, skipping");
                continue;
            }
            var text = ComposeMessage(contact, today, _config.Messages, rng);
            var platforms = PlatformsFor(contact, _config.ChannelOrder);
            if (dryRun)
            {
                var via = platforms.Count > 0 ? string.Join(", ", platforms) : "reminder";
                log.Add($"{contact.Name}: [dry run] would send via {via}: {text}");
                continue;
            }

            string? result = null;
            if (_whatsApp != null && platforms.Count > 0 && platforms[0] == "whatsapp")
            {
                try
                {
                    result = _whatsApp.Send(contact, text);
                }
                catch (DeliveryException e)
                {
                    log.Add($"{contact.Name}: WhatsApp send failed ({e.Message}); falling back to reminder");
                }
            }
            result ??= _reminders.Add(contact, text, platforms);
            sent.MarkSent(contact, today.Year);
            log.Add($"{contact.Name}: {result}");
        }

        if (!dryRun)
            _reminders.Flush($"🎂 Birthday reminders for {today:dddd dd MMMM yyyy}");
        return log;
    }
}
