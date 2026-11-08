import json
import tempfile
import unittest
from datetime import date
from pathlib import Path
from unittest import mock

from birthday_bot import bot
from birthday_bot.channels import ReminderChannel, chat_link
from birthday_bot.importers import import_facebook, import_instagram, import_vcard, normalize_phone
from birthday_bot.models import Birthday, Contact
from birthday_bot.store import load_contacts, merge_contacts, save_contacts


class BirthdayTests(unittest.TestCase):
    def test_parse_forms(self):
        self.assertEqual(Birthday.parse("1990-05-17"), Birthday(5, 17, 1990))
        self.assertEqual(Birthday.parse("19900517"), Birthday(5, 17, 1990))
        self.assertEqual(Birthday.parse("05-17"), Birthday(5, 17))
        self.assertEqual(Birthday.parse("--0517"), Birthday(5, 17))
        self.assertEqual(Birthday.parse("02-29"), Birthday(2, 29))
        self.assertIsNone(Birthday.parse("1991-02-29"))
        self.assertIsNone(Birthday.parse("13-01"))
        self.assertIsNone(Birthday.parse("garbage"))

    def test_leap_day_celebrated_on_feb_28(self):
        b = Birthday(2, 29, 2000)
        self.assertTrue(b.falls_on(date(2027, 2, 28)))
        self.assertFalse(b.falls_on(date(2028, 2, 28)))
        self.assertTrue(b.falls_on(date(2028, 2, 29)))
        self.assertEqual(b.next_occurrence(date(2026, 11, 8)), date(2027, 2, 28))


class ImportTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())

    def test_phone_normalization(self):
        self.assertEqual(normalize_phone("098765 43210", "91"), "+919876543210")
        self.assertEqual(normalize_phone("+44 7700 900123"), "+447700900123")
        self.assertEqual(normalize_phone("0044 7700 900123"), "+447700900123")
        self.assertEqual(normalize_phone("12345"), "")

    def test_vcard(self):
        (self.tmp / "c.vcf").write_text(
            "BEGIN:VCARD\nVERSION:3.0\nFN:Asha Patel\nTEL;TYPE=CELL:098765 43210\nBDAY:1995-11-08\nEND:VCARD\n"
            "BEGIN:VCARD\nVERSION:3.0\nN:Mehta;Rahul;;;\nBDAY:--0314\nEND:VCARD\n"
        )
        a, r = import_vcard(self.tmp / "c.vcf", "91")
        self.assertEqual((a.name, a.whatsapp, a.birthday), ("Asha Patel", "+919876543210", Birthday(11, 8, 1995)))
        self.assertEqual((r.name, r.birthday), ("Rahul Mehta", Birthday(3, 14)))

    def test_facebook_export_fixes_encoding(self):
        (self.tmp / "f.json").write_text(json.dumps({"friends_v2": [{"name": "JosÃ© Silva", "timestamp": 1}]}))
        self.assertEqual(import_facebook(self.tmp / "f.json")[0].name, "José Silva")

    def test_instagram_export(self):
        (self.tmp / "i.json").write_text(json.dumps({"relationships_following": [
            {"title": "", "string_list_data": [{"href": "https://www.instagram.com/rahul_m", "value": "rahul_m"}]},
            {"title": "asha.p", "string_list_data": [{"href": "https://www.instagram.com/_u/asha.p"}]},
        ]}))
        self.assertEqual([c.instagram for c in import_instagram(self.tmp / "i.json")], ["rahul_m", "asha.p"])

    def test_merge_keeps_manual_edits_and_roundtrips(self):
        existing = [Contact("Asha Patel", Birthday(11, 8), whatsapp="+911", sources={"phone"})]
        incoming = [Contact("asha  patel", whatsapp="+922", facebook="asha.fb", sources={"facebook"}), Contact("New Guy")]
        merged, added, updated = merge_contacts(existing, incoming)
        self.assertEqual((added, updated), (1, 1))
        asha = next(c for c in merged if c.key == "asha patel")
        self.assertEqual((asha.whatsapp, asha.facebook, asha.sources), ("+911", "asha.fb", {"phone", "facebook"}))
        save_contacts(self.tmp / "c.csv", merged)
        self.assertEqual([c.name for c in load_contacts(self.tmp / "c.csv")], ["Asha Patel", "New Guy"])


class RunTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.cfg = bot.Config(contacts_file=self.tmp / "c.csv", state_file=self.tmp / "sent.json",
                              messages=["Happy birthday {first_name}, {age} today!"])
        self.today = date(2026, 11, 8)
        self.contacts = [
            Contact("Asha Patel", Birthday(11, 8, 1995), whatsapp="+919876543210", instagram="asha.p", channel="instagram"),
            Contact("Not Today", Birthday(1, 1), whatsapp="+447700900123"),
            Contact("Opted Out", Birthday(11, 8), enabled=False),
        ]

    def test_reminder_with_links_and_no_double_send(self):
        with mock.patch("builtins.print"):
            log = bot.run(self.cfg, self.contacts, self.today)
            self.assertEqual(len(log), 1)
            self.assertIn("reminder queued with 2 link(s)", log[0])
            log2 = bot.run(self.cfg, self.contacts, self.today)
        self.assertIn("already wished", log2[0])

    def test_whatsapp_cloud_used_when_preferred(self):
        self.contacts[0].channel = ""
        wa = mock.Mock()
        wa.send.return_value = "sent via WhatsApp Cloud API (id x)"
        self.cfg.whatsapp = wa
        with mock.patch("builtins.print"):
            log = bot.run(self.cfg, self.contacts, self.today)
        wa.send.assert_called_once()
        self.assertEqual(wa.send.call_args[0][1], "Happy birthday Asha, 31 today!")
        self.assertIn("WhatsApp Cloud API", log[0])

    def test_dry_run_sends_nothing(self):
        log = bot.run(self.cfg, self.contacts, self.today, dry_run=True)
        self.assertIn("[dry run]", log[0])
        self.assertFalse(self.cfg.state_file.exists())

    def test_chat_links(self):
        c = self.contacts[0]
        self.assertEqual(chat_link(c, "whatsapp", "Hi there"), "https://wa.me/919876543210?text=Hi%20there")
        self.assertEqual(chat_link(c, "instagram", "x"), "https://ig.me/m/asha.p")
        self.assertIsNone(chat_link(c, "facebook", "x"))

    def test_reminder_digest(self):
        r = ReminderChannel()
        r.add(self.contacts[0], "HB", ["whatsapp"])
        with mock.patch("builtins.print"):
            body = r.flush("Title")
        self.assertIn("https://wa.me/919876543210?text=HB", body)


if __name__ == "__main__":
    unittest.main()
