using System.Globalization;
using System.Text;

namespace BirthdayBot;

public static class Program
{
    private const string Usage = """
        Birthday Bot - automatic birthday wishes for your friends.

        Usage: BirthdayBot [-c|--config config.json] <command> [options]

        Commands:
          import <vcard|google|facebook|instagram> <file>   import friends from a platform export
          upcoming [--days 30] [--date YYYY-MM-DD]           list upcoming birthdays
          run [--dry-run] [--date YYYY-MM-DD]                send today's wishes (run once a day)
          daemon [--at HH:mm]                                stay running and send wishes daily
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return Execute(args);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine($"error: {e.Message}\n");
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    private static int Execute(string[] argv)
    {
        var args = argv.ToList();
        var configPath = "config.json";
        int ci = args.FindIndex(a => a is "-c" or "--config");
        if (ci >= 0)
        {
            configPath = ci + 1 < args.Count ? args[ci + 1] : throw new UsageException("--config needs a file");
            args.RemoveRange(ci, 2);
        }
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Count == 0 ? 2 : 0;
        }

        var config = BotConfig.Load(configPath);
        var command = args[0];
        var rest = args.Skip(1).ToList();
        return command switch
        {
            "import" => Import(config, rest),
            "upcoming" => Upcoming(config, rest),
            "run" => RunOnce(config, rest),
            "daemon" => Daemon(configPath, rest),
            _ => throw new UsageException($"unknown command '{command}'"),
        };
    }

    private static int Import(BotConfig config, List<string> args)
    {
        if (args.Count != 2 || !Importers.Sources.Contains(args[0]))
            throw new UsageException("import needs a source (vcard, google, facebook, instagram) and a file");
        if (!File.Exists(args[1]))
            throw new UsageException($"file not found: {args[1]}");

        var incoming = Importers.Import(args[0], args[1], config.DefaultCountryCode);
        var (merged, added, updated) = ContactStore.Merge(ContactStore.Load(config.ContactsFile), incoming);
        ContactStore.Save(config.ContactsFile, merged);
        Console.WriteLine($"Imported {incoming.Count} from {args[0]}: {added} new, {updated} updated -> {config.ContactsFile}");
        int missing = merged.Count(c => c.Birthday is null);
        if (missing > 0)
            Console.WriteLine($"{missing} contact(s) have no birthday yet; add them in the 'birthday' column (YYYY-MM-DD or MM-DD).");
        return 0;
    }

    private static int Upcoming(BotConfig config, List<string> args)
    {
        var today = OptionDate(args) ?? DateOnly.FromDateTime(DateTime.Today);
        int days = int.TryParse(Option(args, "--days"), out var d) ? d : 30;
        var soon = ContactStore.Load(config.ContactsFile)
            .Where(c => c.Enabled && c.Birthday is not null)
            .Select(c => (Date: c.Birthday!.Value.NextOccurrence(today), Contact: c))
            .Where(x => x.Date <= today.AddDays(days))
            .OrderBy(x => x.Date)
            .ToList();
        if (soon.Count == 0)
            Console.WriteLine($"No birthdays in the next {days} days.");
        foreach (var (date, c) in soon)
        {
            var channels = Bot.PlatformsFor(c, config.ChannelOrder);
            Console.WriteLine($"{date:ddd dd MMM}  {c.Name,-30} via {(channels.Count > 0 ? string.Join(", ", channels) : "no handle!")}");
        }
        return 0;
    }

    private static int RunOnce(BotConfig config, List<string> args)
    {
        var today = OptionDate(args) ?? DateOnly.FromDateTime(DateTime.Today);
        var log = Bot.FromConfig(config).Run(ContactStore.Load(config.ContactsFile), today, dryRun: args.Contains("--dry-run"));
        Console.WriteLine(log.Count > 0 ? string.Join('\n', log) : $"No birthdays on {today:yyyy-MM-dd}.");
        return 0;
    }

    private static int Daemon(string configPath, List<string> args)
    {
        var at = Option(args, "--at") ?? "09:00";
        if (!TimeOnly.TryParseExact(at, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new UsageException("--at must be HH:mm, e.g. 09:00");

        Console.WriteLine($"Birthday bot running; will check every day at {at}. Ctrl+C to stop.");
        while (true)
        {
            var now = DateTime.Now;
            var target = now.Date + time.ToTimeSpan();
            if (target <= now)
                target = target.AddDays(1);
            Thread.Sleep(target - now);
            try
            {
                var config = BotConfig.Load(configPath); // pick up edits without restarting
                foreach (var line in Bot.FromConfig(config).Run(ContactStore.Load(config.ContactsFile), DateOnly.FromDateTime(DateTime.Today)))
                    Console.WriteLine(line);
            }
            catch (Exception e) // keep the daemon alive; report and retry tomorrow
            {
                Console.Error.WriteLine($"error during run: {e.Message}");
            }
        }
    }

    private static string? Option(List<string> args, string name)
    {
        int i = args.IndexOf(name);
        if (i < 0)
            return null;
        return i + 1 < args.Count ? args[i + 1] : throw new UsageException($"{name} needs a value");
    }

    private static DateOnly? OptionDate(List<string> args)
    {
        var s = Option(args, "--date");
        if (s == null)
            return null;
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new UsageException("--date must be YYYY-MM-DD");
    }

    private sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }
}
