"""Contacts are kept in a plain CSV file you can open and edit in any spreadsheet."""
from __future__ import annotations

import csv
import json
from pathlib import Path
from typing import Iterable

from .models import Birthday, Contact

FIELDS = ["name", "birthday", "whatsapp", "instagram", "facebook", "channel", "message", "enabled", "sources"]


def load_contacts(path: Path) -> list[Contact]:
    if not path.exists():
        return []
    contacts = []
    with path.open(newline="", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            name = (row.get("name") or "").strip()
            if not name:
                continue
            contacts.append(
                Contact(
                    name=name,
                    birthday=Birthday.parse(row.get("birthday") or ""),
                    whatsapp=(row.get("whatsapp") or "").strip(),
                    instagram=(row.get("instagram") or "").strip().lstrip("@"),
                    facebook=(row.get("facebook") or "").strip(),
                    channel=(row.get("channel") or "").strip().lower(),
                    message=(row.get("message") or "").strip(),
                    enabled=(row.get("enabled") or "yes").strip().lower() not in {"no", "false", "0", "n"},
                    sources={s for s in (row.get("sources") or "").split(";") if s},
                )
            )
    return contacts


def save_contacts(path: Path, contacts: Iterable[Contact]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    rows = sorted(contacts, key=lambda c: c.key)
    with path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=FIELDS)
        writer.writeheader()
        for c in rows:
            writer.writerow(
                {
                    "name": c.name,
                    "birthday": str(c.birthday) if c.birthday else "",
                    "whatsapp": c.whatsapp,
                    "instagram": c.instagram,
                    "facebook": c.facebook,
                    "channel": c.channel,
                    "message": c.message,
                    "enabled": "yes" if c.enabled else "no",
                    "sources": ";".join(sorted(c.sources)),
                }
            )


def merge_contacts(existing: list[Contact], incoming: Iterable[Contact]) -> tuple[list[Contact], int, int]:
    """Merge imported contacts into the existing list, matching by name.

    Existing non-empty values are never overwritten, so manual edits are safe.
    Returns (merged, added_count, updated_count).
    """
    by_key = {c.key: c for c in existing}
    added = updated = 0
    for new in incoming:
        cur = by_key.get(new.key)
        if cur is None:
            by_key[new.key] = new
            added += 1
            continue
        changed = False
        for attr in ("birthday", "whatsapp", "instagram", "facebook"):
            if not getattr(cur, attr) and getattr(new, attr):
                setattr(cur, attr, getattr(new, attr))
                changed = True
        if not new.sources <= cur.sources:
            cur.sources |= new.sources
            changed = True
        updated += changed
    return list(by_key.values()), added, updated


class SentLog:
    """Remembers who was already wished this year so re-runs never double-send."""

    def __init__(self, path: Path):
        self.path = path
        self._data: dict[str, list[str]] = {}
        if path.exists():
            self._data = json.loads(path.read_text(encoding="utf-8"))

    def already_sent(self, contact: Contact, year: int) -> bool:
        return contact.key in self._data.get(str(year), [])

    def mark_sent(self, contact: Contact, year: int) -> None:
        self._data.setdefault(str(year), [])
        if contact.key not in self._data[str(year)]:
            self._data[str(year)].append(contact.key)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(self._data, indent=2), encoding="utf-8")
