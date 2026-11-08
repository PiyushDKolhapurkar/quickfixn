using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace BirthdayBot.Tests;

[TestFixture]
public class BirthdayTests
{
    [TestCase("1990-05-17", 5, 17, 1990)]
    [TestCase("19900517", 5, 17, 1990)]
    [TestCase("05-17", 5, 17, null)]
    [TestCase("--0517", 5, 17, null)]
    [TestCase("02-29", 2, 29, null)]
    [TestCase("1990-05-17T00:00:00", 5, 17, 1990)]
    public void ParsesSupportedForms(string text, int month, int day, int? year) =>
        Assert.That(Birthday.Parse(text), Is.EqualTo(new Birthday(month, day, year)));

    [TestCase("1991-02-29")]
    [TestCase("13-01")]
    [TestCase("00-10")]
    [TestCase("garbage")]
    [TestCase("")]
    public void RejectsInvalid(string text) => Assert.That(Birthday.Parse(text), Is.Null);

    [Test]
    public void LeapDayCelebratedOnFeb28InNonLeapYears()
    {
        var b = new Birthday(2, 29, 2000);
        Assert.That(b.FallsOn(new DateOnly(2027, 2, 28)), Is.True);
        Assert.That(b.FallsOn(new DateOnly(2028, 2, 28)), Is.False);
        Assert.That(b.FallsOn(new DateOnly(2028, 2, 29)), Is.True);
        Assert.That(b.NextOccurrence(new DateOnly(2026, 11, 8)), Is.EqualTo(new DateOnly(2027, 2, 28)));
    }
}

[TestFixture]
public class ImportTests
{
    private string _dir = "";

    [SetUp]
    public void SetUp() => _dir = Directory.CreateTempSubdirectory("bbot").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [TestCase("098765 43210", "91", "+919876543210")]
    [TestCase("+44 7700 900123", "", "+447700900123")]
    [TestCase("0044 7700 900123", "", "+447700900123")]
    [TestCase("12345", "", "")]
    public void NormalizesPhones(string raw, string cc, string expected) =>
        Assert.That(Importers.NormalizePhone(raw, cc), Is.EqualTo(expected));

    [Test]
    public void ImportsVCard()
    {
        var path = Write("c.vcf",
            "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Asha Patel\r\nTEL;TYPE=CELL:098765 43210\r\nBDAY:1995-11-08\r\nEND:VCARD\r\n" +
            "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Mehta;Rahul;;;\r\nBDAY:--0314\r\nEND:VCARD\r\n");
        var contacts = Importers.ImportVCard(path, "91");
        Assert.That(contacts, Has.Count.EqualTo(2));
        Assert.That(contacts[0].Name, Is.EqualTo("Asha Patel"));
        Assert.That(contacts[0].WhatsApp, Is.EqualTo("+919876543210"));
        Assert.That(contacts[0].Birthday, Is.EqualTo(new Birthday(11, 8, 1995)));
        Assert.That(contacts[1].Name, Is.EqualTo("Rahul Mehta"));
        Assert.That(contacts[1].Birthday, Is.EqualTo(new Birthday(3, 14)));
    }

    [Test]
    public void ImportsGoogleCsv()
    {
        var path = Write("g.csv",
            "﻿Name,Given Name,Family Name,Birthday,Phone 1 - Type,Phone 1 - Value\n" +
            "Asha Patel,Asha,Patel,1995-11-08,Mobile,+91 98765 43210 ::: +91 11111 11111\n" +
            ",Rahul,Mehta,--03-14,,\n");
        var contacts = Importers.ImportGoogleCsv(path);
        Assert.That(contacts[0].WhatsApp, Is.EqualTo("+919876543210"));
        Assert.That(contacts[1].Name, Is.EqualTo("Rahul Mehta"));
        Assert.That(contacts[1].Birthday, Is.EqualTo(new Birthday(3, 14)));
    }

    [Test]
    public void ImportsFacebookAndFixesEncoding()
    {
        // Meta exports "José" as the UTF-8 bytes written as latin-1 escapes.
        var path = Write("f.json", "{\"friends_v2\":[{\"name\":\"Jos\\u00c3\\u00a9 Silva\",\"timestamp\":1}]}");
        Assert.That(Importers.ImportFacebook(path).Single().Name, Is.EqualTo("José Silva"));
    }

    [Test]
    public void ImportsInstagram()
    {
        var path = Write("i.json", JsonSerializer.Serialize(new
        {
            relationships_following = new object[]
            {
                new { title = "", string_list_data = new[] { new { href = "https://www.instagram.com/rahul_m", value = "rahul_m" } } },
                new { title = "asha.p", string_list_data = new[] { new { href = "https://www.instagram.com/_u/asha.p" } } },
            },
        }));
        Assert.That(Importers.ImportInstagram(path).Select(c => c.Instagram), Is.EqualTo(new[] { "rahul_m", "asha.p" }));
    }

    [Test]
    public void MergeKeepsManualEditsAndRoundTrips()
    {
        var existing = new List<Contact>
        {
            new("Asha Patel") { Birthday = new Birthday(11, 8), WhatsApp = "+911", Sources = { "phone" } },
        };
        var incoming = new[]
        {
            new Contact("asha  patel") { WhatsApp = "+922", Facebook = "asha.fb", Sources = { "facebook" } },
            new Contact("New Guy"),
        };
        var (merged, added, updated) = ContactStore.Merge(existing, incoming);
        Assert.That((added, updated), Is.EqualTo((1, 1)));
        var asha = merged.Single(c => c.Key == "asha patel");
        Assert.That(asha.WhatsApp, Is.EqualTo("+911"));
        Assert.That(asha.Facebook, Is.EqualTo("asha.fb"));
        Assert.That(asha.Sources, Is.EquivalentTo(new[] { "phone", "facebook" }));

        asha.Message = "Happy birthday, \"Ash\", see you at 8, ok?";
        var path = Path.Combine(_dir, "contacts.csv");
        ContactStore.Save(path, merged);
        var loaded = ContactStore.Load(path);
        Assert.That(loaded.Select(c => c.Name), Is.EqualTo(new[] { "Asha Patel", "New Guy" }));
        Assert.That(loaded[0].Message, Is.EqualTo(asha.Message));
        Assert.That(loaded[0].Birthday, Is.EqualTo(new Birthday(11, 8)));
    }
}

[TestFixture]
public class RunTests
{
    private string _dir = "";
    private BotConfig _config = null!;
    private StringWriter _out = null!;
    private List<Contact> _contacts = null!;
    private static readonly DateOnly Today = new(2026, 11, 8);

    private sealed class FakeSender : IAutoSender
    {
        public List<(Contact Contact, string Text)> Calls { get; } = new();
        public bool Fail { get; set; }

        public string Send(Contact contact, string text)
        {
            Calls.Add((contact, text));
            if (Fail)
                throw new DeliveryException("boom");
            return "sent via WhatsApp Cloud API (id x)";
        }
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Directory.CreateTempSubdirectory("bbot").FullName;
        _config = new BotConfig
        {
            ContactsFile = Path.Combine(_dir, "c.csv"),
            StateFile = Path.Combine(_dir, "sent.json"),
            Messages = new() { "Happy birthday {first_name}, {age} today!" },
        };
        _out = new StringWriter();
        _contacts = new()
        {
            new("Asha Patel") { Birthday = new Birthday(11, 8, 1995), WhatsApp = "+919876543210", Instagram = "asha.p", Channel = "instagram" },
            new("Not Today") { Birthday = new Birthday(1, 1), WhatsApp = "+447700900123" },
            new("Opted Out") { Birthday = new Birthday(11, 8), Enabled = false },
        };
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    private Bot MakeBot(IAutoSender? sender = null) => new(_config, sender, new ReminderChannel(output: _out));

    [Test]
    public void SendsReminderWithLinksAndNeverDoubleSends()
    {
        var log = MakeBot().Run(_contacts, Today);
        Assert.That(log, Has.Count.EqualTo(1));
        Assert.That(log[0], Does.Contain("reminder queued with 2 link(s)"));
        Assert.That(_out.ToString(), Does.Contain("https://ig.me/m/asha.p"));

        var again = MakeBot().Run(_contacts, Today);
        Assert.That(again.Single(), Does.Contain("already wished"));
    }

    [Test]
    public void UsesWhatsAppCloudWhenItIsThePreferredChannel()
    {
        _contacts[0].Channel = "";
        var sender = new FakeSender();
        var log = MakeBot(sender).Run(_contacts, Today);
        Assert.That(sender.Calls.Single().Text, Is.EqualTo("Happy birthday Asha, 31 today!"));
        Assert.That(log.Single(), Does.Contain("WhatsApp Cloud API"));
    }

    [Test]
    public void FallsBackToReminderWhenWhatsAppFails()
    {
        _contacts[0].Channel = "";
        var log = MakeBot(new FakeSender { Fail = true }).Run(_contacts, Today);
        Assert.That(log[0], Does.Contain("falling back to reminder"));
        Assert.That(log[1], Does.Contain("reminder queued"));
        Assert.That(_out.ToString(), Does.Contain("https://wa.me/919876543210?text="));
    }

    [Test]
    public void DryRunSendsNothing()
    {
        var log = MakeBot().Run(_contacts, Today, dryRun: true);
        Assert.That(log.Single(), Does.Contain("[dry run]"));
        Assert.That(File.Exists(_config.StateFile), Is.False);
        Assert.That(_out.ToString(), Is.Empty);
    }

    [Test]
    public void BuildsChatLinks()
    {
        var c = _contacts[0];
        Assert.That(ChatLinks.For(c, "whatsapp", "Hi there"), Is.EqualTo("https://wa.me/919876543210?text=Hi%20there"));
        Assert.That(ChatLinks.For(c, "instagram", "x"), Is.EqualTo("https://ig.me/m/asha.p"));
        Assert.That(ChatLinks.For(c, "facebook", "x"), Is.Null);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[{\"id\":\"wamid.123\"}]}", Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    [Test]
    public void WhatsAppCloudSendsTemplateRequest()
    {
        var handler = new RecordingHandler();
        var settings = new WhatsAppCloudSettings { PhoneNumberId = "555", TemplateName = "birthday_wish" };
        var result = new WhatsAppCloudChannel(settings, "tok", new HttpClient(handler)).Send(_contacts[0], "ignored");

        Assert.That(result, Does.Contain("wamid.123"));
        Assert.That(handler.Request!.RequestUri!.ToString(), Is.EqualTo("https://graph.facebook.com/v21.0/555/messages"));
        Assert.That(handler.Request.Headers.Authorization!.ToString(), Is.EqualTo("Bearer tok"));
        using var doc = JsonDocument.Parse(handler.Body);
        var root = doc.RootElement;
        Assert.That(root.GetProperty("to").GetString(), Is.EqualTo("919876543210"));
        Assert.That(root.GetProperty("template").GetProperty("name").GetString(), Is.EqualTo("birthday_wish"));
        Assert.That(root.GetProperty("template").GetProperty("components")[0].GetProperty("parameters")[0].GetProperty("text").GetString(),
            Is.EqualTo("Asha"));
    }
}
