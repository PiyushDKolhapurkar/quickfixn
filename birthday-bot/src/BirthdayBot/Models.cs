using System.Text.RegularExpressions;

namespace BirthdayBot;

/// <summary>A birthday; the year is optional (needed only for {age}).</summary>
public readonly record struct Birthday(int Month, int Day, int? Year = null)
{
    private static readonly Regex Pattern = new(@"^(?:(\d{4}|-{1,2})-?)?(\d{2})-?(\d{2})$", RegexOptions.Compiled);

    /// <summary>Parses "YYYY-MM-DD", "MM-DD", "YYYYMMDD" or vCard "--MMDD". Returns null if invalid.</summary>
    public static Birthday? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim().Split('T')[0];
        var m = Pattern.Match(text);
        if (!m.Success)
            return null;

        int? year = m.Groups[1].Success && char.IsDigit(m.Groups[1].Value[0]) ? int.Parse(m.Groups[1].Value) : null;
        int month = int.Parse(m.Groups[2].Value);
        int day = int.Parse(m.Groups[3].Value);
        if (month is < 1 or > 12 || (year is < 1 or > 9999))
            return null;
        // Check against a leap year when there is no year, so Feb 29 is accepted.
        if (day < 1 || day > DateTime.DaysInMonth(year ?? 2000, month))
            return null;
        return new Birthday(month, day, year);
    }

    /// <summary>True if this birthday is celebrated on <paramref name="today"/>.
    /// Feb 29 birthdays are celebrated on Feb 28 in non-leap years.</summary>
    public bool FallsOn(DateOnly today) =>
        (Month == today.Month && Day == today.Day) ||
        (Month == 2 && Day == 29 && today.Month == 2 && today.Day == 28 && !DateTime.IsLeapYear(today.Year));

    public DateOnly NextOccurrence(DateOnly after)
    {
        for (int year = after.Year; ; year++)
        {
            int day = Month == 2 && Day == 29 && !DateTime.IsLeapYear(year) ? 28 : Day;
            var candidate = new DateOnly(year, Month, day);
            if (candidate >= after)
                return candidate;
        }
    }

    public int? AgeOn(DateOnly today) => Year is { } y ? today.Year - y : null;

    public override string ToString() => Year is { } y ? $"{y:D4}-{Month:D2}-{Day:D2}" : $"{Month:D2}-{Day:D2}";
}

public sealed class Contact
{
    public Contact(string name) => Name = name;

    public string Name { get; set; }
    public Birthday? Birthday { get; set; }
    /// <summary>Phone number in international format, e.g. +919876543210.</summary>
    public string WhatsApp { get; set; } = "";
    public string Instagram { get; set; } = "";
    /// <summary>Facebook username / profile id (used for m.me links).</summary>
    public string Facebook { get; set; } = "";
    /// <summary>Preferred channel: whatsapp | instagram | facebook (blank = automatic).</summary>
    public string Channel { get; set; } = "";
    /// <summary>Optional custom message for this person.</summary>
    public string Message { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public SortedSet<string> Sources { get; set; } = new(StringComparer.Ordinal);

    public string FirstName
    {
        get
        {
            var parts = Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : Name;
        }
    }

    /// <summary>Case- and whitespace-insensitive identity used to match contacts across imports.</summary>
    public string Key => string.Join(' ', Name.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public string HandleFor(string platform) => platform switch
    {
        "whatsapp" => WhatsApp,
        "instagram" => Instagram,
        "facebook" => Facebook,
        _ => "",
    };
}
