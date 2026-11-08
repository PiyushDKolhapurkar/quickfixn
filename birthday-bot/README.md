# Birthday Bot 🎂

Gathers your friends from Facebook, Instagram and your WhatsApp/phone
contacts, remembers their birthdays, and wishes them on the day.

Written in C# (.NET 8). It uses only the standard .NET libraries; nothing else to install.

## What each platform allows

None of these platforms lets a personal account read its friends' birthdays
or send DMs through an API. Scraping or using unofficial clients breaks their
Terms of Service and often gets the account locked. So the bot only uses
official routes:

| Platform | Getting friends | Sending the wish |
|---|---|---|
| **WhatsApp** | Your phone's contacts exported as `.vcf`, or Google Contacts CSV. Birthdays come with them. | **Automatic** through the official [WhatsApp Cloud API](https://developers.facebook.com/docs/whatsapp/cloud-api) (needs a Business number and an approved template). Otherwise you get a one-tap `wa.me` link with the message already typed. |
| **Facebook** | Names from *Download your information* → `your_friends.json` (the export has no birthdays). | One-tap `m.me` link reminder. Messenger has no API for personal accounts. |
| **Instagram** | Usernames from *Download your information* → `following.json` / `followers_1.json`. | One-tap `ig.me` link reminder. The official API can't start a DM. |

In practice, WhatsApp wishes can go out on their own. For everything else,
the bot sends you a morning digest (printed to the console and optionally
emailed), and each wish takes one tap.

## Setup

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download), then:

```bash
cd birthday-bot
dotnet publish src/BirthdayBot -c Release -o out   # builds ./out/BirthdayBot
cp config.example.json config.json                 # edit as needed
```

The examples below use `./out/BirthdayBot`. On Windows use `out\BirthdayBot.exe`.
While developing you can use `dotnet run --project src/BirthdayBot -- <command>` instead.

### 1. Import your friends

```bash
# Phone / WhatsApp contacts (Android: Contacts > Export; iPhone: iCloud.com > Export vCard)
./out/BirthdayBot import vcard ~/Downloads/contacts.vcf

# Google Contacts (contacts.google.com > Export > Google CSV)
./out/BirthdayBot import google ~/Downloads/contacts.csv

# Facebook: Settings > Download your information > JSON
./out/BirthdayBot import facebook ~/fb/connections/friends/your_friends.json

# Instagram: Accounts Center > Download your information > JSON
./out/BirthdayBot import instagram ~/ig/connections/followers_and_following/following.json
```

Everything is merged by name into `data/contacts.csv`. Importing again won't
overwrite your manual edits.

### 2. Fill in the gaps in `data/contacts.csv`

| column | meaning |
|---|---|
| `birthday` | `YYYY-MM-DD` (gives `{age}`) or `MM-DD` |
| `whatsapp` | `+<country><number>` |
| `instagram` / `facebook` | username (Facebook: the part after `facebook.com/`) |
| `channel` | preferred channel: `whatsapp`, `instagram` or `facebook` |
| `message` | custom wish for this person (supports `{first_name}`, `{name}`, `{age}`) |
| `enabled` | `no` to skip someone |

Facebook and Instagram entries won't have a birthday until you add one, or
until a contact import with the same name brings one in. If someone appears
twice (for example an Instagram username and their real name), merge the two
rows.

See `contacts.example.csv` for an example.

### 3. Check it

```bash
./out/BirthdayBot upcoming --days 30
./out/BirthdayBot run --dry-run
```

### 4. Run it every day

Use either cron:

```cron
0 9 * * *  cd /path/to/birthday-bot && ./out/BirthdayBot run >> bot.log 2>&1
```

(on Windows, use Task Scheduler to run `BirthdayBot.exe run` daily), or a long-running process:

```bash
./out/BirthdayBot daemon --at 09:00
```

The bot remembers who it has already wished each year in `data/sent.json`,
so running it more than once a day is safe. People born on Feb 29 are wished
on Feb 28 in non-leap years.

## Optional: fully automatic WhatsApp

1. Create a Meta developer app and add the WhatsApp product. Register a
   business phone number.
2. Create a message template, e.g. `birthday_wish` with the body
   `Happy birthday {{1}}! 🎉 Have a wonderful day.`, and wait for approval.
3. In `config.json`, set `"whatsAppCloud": { "enabled": true, ... }` and fill in
   `phone_number_id` and `template_name`. Set the `WHATSAPP_TOKEN` environment variable to your access token.

The bot fills `{{1}}` with the person's first name. If a send fails, it falls
back to a reminder link, so nobody gets missed. Meta's rules require that
recipients have agreed to get messages from your business number.

## Email reminders

Fill in the `reminders` section of `config.json`. For Gmail, use `smtp.gmail.com` and
an app password in `SMTP_PASSWORD`.

## Tests

```bash
dotnet test
```
