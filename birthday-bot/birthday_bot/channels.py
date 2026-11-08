"""Ways of delivering a birthday wish.

* WhatsAppCloudChannel - fully automatic, via Meta's official WhatsApp Cloud
  API. Needs a WhatsApp Business phone number and an approved message
  template (business-initiated messages must use a template).
* ReminderChannel - for everything that cannot legally be automated (personal
  Facebook Messenger, Instagram DMs, personal WhatsApp): sends *you* a
  reminder containing a one-tap link that opens the chat with the message
  pre-filled, so wishing someone takes a single tap.
"""
from __future__ import annotations

import json
import smtplib
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from email.message import EmailMessage
from typing import Optional

from .models import Contact

GRAPH_API = "https://graph.facebook.com"


class DeliveryError(Exception):
    pass


def chat_link(contact: Contact, platform: str, text: str) -> Optional[str]:
    """A link that opens a chat with `contact` on `platform` (None if unknown)."""
    if platform == "whatsapp" and contact.whatsapp:
        number = contact.whatsapp.lstrip("+")
        return f"https://wa.me/{number}?text={urllib.parse.quote(text)}"
    if platform == "instagram" and contact.instagram:
        return f"https://ig.me/m/{urllib.parse.quote(contact.instagram)}"
    if platform == "facebook" and contact.facebook:
        return f"https://m.me/{urllib.parse.quote(contact.facebook)}"
    return None


@dataclass
class WhatsAppCloudChannel:
    access_token: str
    phone_number_id: str
    template_name: str = ""          # e.g. "birthday_wish"; blank -> send plain text
    template_language: str = "en"
    api_version: str = "v21.0"
    timeout: float = 20.0

    def send(self, contact: Contact, text: str) -> str:
        if not contact.whatsapp:
            raise DeliveryError(f"{contact.name} has no WhatsApp number")
        to = contact.whatsapp.lstrip("+")
        if self.template_name:
            payload = {
                "messaging_product": "whatsapp",
                "to": to,
                "type": "template",
                "template": {
                    "name": self.template_name,
                    "language": {"code": self.template_language},
                    "components": [
                        {"type": "body", "parameters": [{"type": "text", "text": contact.first_name}]}
                    ],
                },
            }
        else:
            # Free-form text only delivers if the person messaged you in the last 24h.
            payload = {"messaging_product": "whatsapp", "to": to, "type": "text", "text": {"body": text}}
        req = urllib.request.Request(
            f"{GRAPH_API}/{self.api_version}/{self.phone_number_id}/messages",
            data=json.dumps(payload).encode(),
            headers={"Authorization": f"Bearer {self.access_token}", "Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                body = json.loads(resp.read() or b"{}")
        except urllib.error.HTTPError as e:
            raise DeliveryError(f"WhatsApp API error {e.code}: {e.read().decode(errors='replace')}") from e
        except urllib.error.URLError as e:
            raise DeliveryError(f"WhatsApp API unreachable: {e.reason}") from e
        msg_id = (body.get("messages") or [{}])[0].get("id", "?")
        return f"sent via WhatsApp Cloud API (id {msg_id})"


@dataclass
class ReminderChannel:
    """Collects reminders and delivers them as one digest (console and/or email)."""

    email_to: str = ""
    smtp_host: str = ""
    smtp_port: int = 587
    smtp_user: str = ""
    smtp_password: str = ""
    email_from: str = ""

    def __post_init__(self) -> None:
        self.items: list[str] = []

    def add(self, contact: Contact, text: str, platforms: list[str]) -> str:
        links = [f"  {p}: {link}" for p in platforms if (link := chat_link(contact, p, text))]
        entry = f"* {contact.name}\n  message: {text}"
        entry += "\n" + "\n".join(links) if links else "\n  (no chat handle on file - add one in contacts.csv)"
        self.items.append(entry)
        return "reminder queued" + (f" with {len(links)} link(s)" if links else "")

    def flush(self, title: str) -> str:
        if not self.items:
            return ""
        body = title + "\n\n" + "\n\n".join(self.items) + "\n"
        print(body)
        if self.email_to and self.smtp_host:
            msg = EmailMessage()
            msg["Subject"] = title
            msg["From"] = self.email_from or self.smtp_user
            msg["To"] = self.email_to
            msg.set_content(body)
            with smtplib.SMTP(self.smtp_host, self.smtp_port, timeout=30) as s:
                s.starttls()
                if self.smtp_user:
                    s.login(self.smtp_user, self.smtp_password)
                s.send_message(msg)
        self.items = []
        return body
