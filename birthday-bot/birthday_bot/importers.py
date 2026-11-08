"""Import friends/contacts from the data exports each platform officially provides.

None of Facebook, Instagram or WhatsApp offers an API that lets a personal
account read its friend list *with birthdays*, so the bot works from exports:

* Phone / WhatsApp contacts -> export a .vcf file (vCards carry BDAY).
  WhatsApp uses your phone's address book, so this is your WhatsApp list.
* Google Contacts           -> contacts.google.com > Export > Google CSV.
* Facebook                  -> Settings > "Download your information" (JSON),
                               file connections/friends/your_friends.json.
* Instagram                 -> Accounts Center > "Download your information"
                               (JSON), file connections/followers_and_following/
                               following.json (and/or followers_1.json).

Facebook and Instagram exports contain names/usernames but no birthdays, so
fill those in afterwards in contacts.csv (or let a vCard/Google import that
has the same name supply them).
"""
from __future__ import annotations

import csv
import json
import re
from pathlib import Path
from typing import Iterator

from .models import Birthday, Contact


def _fix_mojibake(text: str) -> str:
    """Meta's JSON exports encode UTF-8 bytes as latin-1 escapes; undo that."""
    try:
        return text.encode("latin-1").decode("utf-8")
    except (UnicodeEncodeError, UnicodeDecodeError):
        return text


def normalize_phone(raw: str, default_country_code: str = "") -> str:
    """Return a phone number as '+<digits>' or '' if it does not look valid."""
    raw = raw.strip()
    if not raw:
        return ""
    has_plus = raw.startswith("+") or raw.startswith("00")
    digits = re.sub(r"\D", "", raw)
    if raw.startswith("00"):
        digits = digits[2:]
    if not has_plus:
        cc = default_country_code.lstrip("+")
        if not cc:
            return "+" + digits if len(digits) > 10 else ""
        digits = cc + digits.lstrip("0")
    return "+" + digits if 8 <= len(digits) <= 15 else ""


# ---------------------------------------------------------------- vCard (.vcf)

def _unfold(lines: list[str]) -> Iterator[str]:
    buf = ""
    for line in lines:
        if line[:1] in (" ", "\t") and buf:
            buf += line[1:]
        else:
            if buf:
                yield buf
            buf = line
    if buf:
        yield buf


def import_vcard(path: Path, default_country_code: str = "") -> list[Contact]:
    text = path.read_text(encoding="utf-8", errors="replace")
    contacts: list[Contact] = []
    card: dict[str, list[str]] = {}
    for line in _unfold(text.splitlines()):
        if ":" not in line:
            continue
        head, value = line.split(":", 1)
        prop = head.split(";")[0].split(".")[-1].upper()
        if prop == "BEGIN":
            card = {}
        elif prop == "END":
            name = (card.get("FN") or [""])[0].strip()
            if not name and card.get("N"):
                parts = card["N"][0].split(";")
                name = " ".join(p for p in (parts[1:2] + parts[:1]) if p).strip()
            if name:
                phones = [normalize_phone(p, default_country_code) for p in card.get("TEL", [])]
                contacts.append(
                    Contact(
                        name=name,
                        birthday=Birthday.parse((card.get("BDAY") or [""])[0]),
                        whatsapp=next((p for p in phones if p), ""),
                        sources={"phone"},
                    )
                )
        else:
            card.setdefault(prop, []).append(value.replace("\\,", ",").replace("\\;", ";"))
    return contacts


# ------------------------------------------------------------ Google Contacts

def import_google_csv(path: Path, default_country_code: str = "") -> list[Contact]:
    contacts = []
    with path.open(newline="", encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            name = (row.get("Name") or " ".join(
                filter(None, [row.get("First Name") or row.get("Given Name"), row.get("Last Name") or row.get("Family Name")])
            )).strip()
            if not name:
                continue
            phone = ""
            for key, val in row.items():
                if key and "Phone" in key and "Value" in key and val:
                    phone = normalize_phone(val.split(":::")[0], default_country_code)
                    if phone:
                        break
            contacts.append(
                Contact(name=name, birthday=Birthday.parse(row.get("Birthday") or ""), whatsapp=phone, sources={"google"})
            )
    return contacts


# --------------------------------------------------------------- Facebook

def import_facebook(path: Path) -> list[Contact]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if isinstance(data, dict):
        entries = data.get("friends_v2") or data.get("friends") or []
    else:
        entries = data
    return [
        Contact(name=_fix_mojibake(e["name"]).strip(), sources={"facebook"})
        for e in entries
        if isinstance(e, dict) and e.get("name")
    ]


# --------------------------------------------------------------- Instagram

def import_instagram(path: Path) -> list[Contact]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if isinstance(data, dict):
        entries = next((v for k, v in data.items() if k.startswith("relationships_")), [])
    else:
        entries = data
    contacts = []
    for e in entries:
        items = e.get("string_list_data") or [{}]
        username = (items[0].get("value") or e.get("title") or "").strip()
        if not username:
            href = items[0].get("href", "")
            username = href.rstrip("/").rsplit("/", 1)[-1]
        if username:
            username = _fix_mojibake(username).lstrip("@")
            contacts.append(Contact(name=username, instagram=username, sources={"instagram"}))
    return contacts


IMPORTERS = {
    "vcard": import_vcard,
    "google": import_google_csv,
    "facebook": import_facebook,
    "instagram": import_instagram,
}
