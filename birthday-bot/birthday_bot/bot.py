"""Decides who has a birthday today and dispatches the wishes."""
from __future__ import annotations

import os
import random
import tomllib
from dataclasses import dataclass, field
from datetime import date
from pathlib import Path
from typing import Optional

from .channels import DeliveryError, ReminderChannel, WhatsAppCloudChannel
from .models import Contact
from .store import SentLog

DEFAULT_MESSAGES = [
    "Happy birthday, {first_name}! 🎉 Wishing you a fantastic year ahead.",
    "Happy birthday {first_name}! 🎂 Hope your day is as awesome as you are.",
    "Many happy returns of the day, {first_name}! 🥳 Have a wonderful celebration.",
]


@dataclass
class Config:
    contacts_file: Path
    state_file: Path
    messages: list[str] = field(default_factory=lambda: list(DEFAULT_MESSAGES))
    default_country_code: str = ""
    channel_order: list[str] = field(default_factory=lambda: ["whatsapp", "instagram", "facebook"])
    whatsapp: Optional[WhatsAppCloudChannel] = None
    reminder: ReminderChannel = field(default_factory=ReminderChannel)

    @classmethod
    def load(cls, path: Optional[Path]) -> "Config":
        raw: dict = {}
        base = Path.cwd()
        if path and path.exists():
            raw = tomllib.loads(path.read_text(encoding="utf-8"))
            base = path.parent
        general = raw.get("general", {})
        wa = raw.get("whatsapp_cloud", {})
        rem = raw.get("reminders", {})

        token = os.environ.get("WHATSAPP_TOKEN") or wa.get("access_token", "")
        phone_id = os.environ.get("WHATSAPP_PHONE_NUMBER_ID") or wa.get("phone_number_id", "")
        whatsapp = None
        if wa.get("enabled", False) and token and phone_id:
            whatsapp = WhatsAppCloudChannel(
                access_token=token,
                phone_number_id=phone_id,
                template_name=wa.get("template_name", ""),
                template_language=wa.get("template_language", "en"),
                api_version=wa.get("api_version", "v21.0"),
            )
        reminder = ReminderChannel(
            email_to=rem.get("email_to", ""),
            smtp_host=rem.get("smtp_host", ""),
            smtp_port=int(rem.get("smtp_port", 587)),
            smtp_user=rem.get("smtp_user", ""),
            smtp_password=os.environ.get("SMTP_PASSWORD") or rem.get("smtp_password", ""),
            email_from=rem.get("email_from", ""),
        )
        return cls(
            contacts_file=base / general.get("contacts_file", "data/contacts.csv"),
            state_file=base / general.get("state_file", "data/sent.json"),
            messages=general.get("messages") or list(DEFAULT_MESSAGES),
            default_country_code=str(general.get("default_country_code", "")),
            channel_order=general.get("channel_order") or ["whatsapp", "instagram", "facebook"],
            whatsapp=whatsapp,
            reminder=reminder,
        )


def compose_message(contact: Contact, today: date, templates: list[str], rng: random.Random) -> str:
    template = contact.message or rng.choice(templates)
    age = contact.birthday.age_on(today) if contact.birthday else None
    return template.format(
        name=contact.name,
        first_name=contact.first_name,
        age=age if age is not None else "",
    )


def platforms_for(contact: Contact, order: list[str]) -> list[str]:
    handles = {"whatsapp": contact.whatsapp, "instagram": contact.instagram, "facebook": contact.facebook}
    available = [p for p in order if handles.get(p)]
    if contact.channel in available:
        available.remove(contact.channel)
        available.insert(0, contact.channel)
    return available


def birthdays_on(contacts: list[Contact], today: date) -> list[Contact]:
    return [c for c in contacts if c.enabled and c.birthday and c.birthday.falls_on(today)]


def run(cfg: Config, contacts: list[Contact], today: date, dry_run: bool = False) -> list[str]:
    """Wish everyone whose birthday is `today`. Returns a log of what happened."""
    log: list[str] = []
    sent = SentLog(cfg.state_file)
    rng = random.Random(f"{today.isoformat()}")
    for contact in birthdays_on(contacts, today):
        if sent.already_sent(contact, today.year):
            log.append(f"{contact.name}: already wished this year, skipping")
            continue
        text = compose_message(contact, today, cfg.messages, rng)
        platforms = platforms_for(contact, cfg.channel_order)
        if dry_run:
            log.append(f"{contact.name}: [dry run] would send via {platforms or ['reminder']}: {text}")
            continue

        result = None
        if cfg.whatsapp and platforms and platforms[0] == "whatsapp":
            try:
                result = cfg.whatsapp.send(contact, text)
            except DeliveryError as e:
                log.append(f"{contact.name}: WhatsApp send failed ({e}); falling back to reminder")
        if result is None:
            result = cfg.reminder.add(contact, text, platforms)
        sent.mark_sent(contact, today.year)
        log.append(f"{contact.name}: {result}")

    if not dry_run:
        cfg.reminder.flush(f"🎂 Birthday reminders for {today:%A %d %B %Y}")
    return log
