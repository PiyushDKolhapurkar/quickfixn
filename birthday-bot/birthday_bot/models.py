"""Core data types: a contact and its birthday."""
from __future__ import annotations

import calendar
import re
from dataclasses import dataclass, field
from datetime import date
from typing import Optional

_BDAY_RE = re.compile(r"^(?:(\d{4}|-{1,2})-?)?(\d{2})-?(\d{2})$")


@dataclass(frozen=True)
class Birthday:
    month: int
    day: int
    year: Optional[int] = None

    @classmethod
    def parse(cls, text: str) -> Optional["Birthday"]:
        """Parse 'YYYY-MM-DD', 'MM-DD', 'YYYYMMDD', or vCard '--MMDD' forms."""
        if not text:
            return None
        text = text.strip().split("T")[0]
        m = _BDAY_RE.match(text)
        if not m:
            return None
        year_s, month_s, day_s = m.groups()
        year = int(year_s) if year_s and year_s.isdigit() else None
        month, day = int(month_s), int(day_s)
        if not 1 <= month <= 12:
            return None
        # Validate against a leap year so Feb 29 is accepted without a year.
        if not 1 <= day <= calendar.monthrange(year or 2000, month)[1]:
            return None
        return cls(month, day, year)

    def falls_on(self, today: date) -> bool:
        """True if this birthday is celebrated on `today`.

        Feb 29 birthdays are celebrated on Feb 28 in non-leap years.
        """
        if (self.month, self.day) == (today.month, today.day):
            return True
        return (
            (self.month, self.day) == (2, 29)
            and (today.month, today.day) == (2, 28)
            and not calendar.isleap(today.year)
        )

    def next_occurrence(self, after: date) -> date:
        for year in (after.year, after.year + 1, after.year + 2, after.year + 3, after.year + 4):
            day = self.day
            if (self.month, day) == (2, 29) and not calendar.isleap(year):
                day = 28
            candidate = date(year, self.month, day)
            if candidate >= after:
                return candidate
        raise AssertionError("unreachable")

    def age_on(self, today: date) -> Optional[int]:
        return today.year - self.year if self.year else None

    def __str__(self) -> str:
        if self.year:
            return f"{self.year:04d}-{self.month:02d}-{self.day:02d}"
        return f"{self.month:02d}-{self.day:02d}"


@dataclass
class Contact:
    name: str
    birthday: Optional[Birthday] = None
    whatsapp: str = ""       # phone number in international format, e.g. +919876543210
    instagram: str = ""      # Instagram username
    facebook: str = ""       # Facebook username / profile id (used for m.me links)
    channel: str = ""        # preferred channel: whatsapp | instagram | facebook (blank = auto)
    message: str = ""        # optional per-contact custom message
    enabled: bool = True
    sources: set[str] = field(default_factory=set)

    @property
    def first_name(self) -> str:
        return self.name.split()[0] if self.name.strip() else self.name

    @property
    def key(self) -> str:
        return " ".join(self.name.lower().split())
