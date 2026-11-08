"""Command line interface:  python -m birthday_bot <command> ..."""
from __future__ import annotations

import argparse
import sys
import time
from datetime import date, datetime, timedelta
from pathlib import Path

from . import bot
from .importers import IMPORTERS
from .store import load_contacts, merge_contacts, save_contacts


def _parse_date(text: str) -> date:
    return datetime.strptime(text, "%Y-%m-%d").date()


def cmd_import(args, cfg: bot.Config) -> int:
    importer = IMPORTERS[args.source]
    if args.source in ("vcard", "google"):
        incoming = importer(Path(args.file), cfg.default_country_code)
    else:
        incoming = importer(Path(args.file))
    merged, added, updated = merge_contacts(load_contacts(cfg.contacts_file), incoming)
    save_contacts(cfg.contacts_file, merged)
    missing = sum(1 for c in merged if not c.birthday)
    print(f"Imported {len(incoming)} from {args.source}: {added} new, {updated} updated -> {cfg.contacts_file}")
    if missing:
        print(f"{missing} contact(s) have no birthday yet; add them in the 'birthday' column (YYYY-MM-DD or MM-DD).")
    return 0


def cmd_upcoming(args, cfg: bot.Config) -> int:
    today = args.date or date.today()
    contacts = [c for c in load_contacts(cfg.contacts_file) if c.enabled and c.birthday]
    soon = sorted(
        ((c.birthday.next_occurrence(today), c) for c in contacts),
        key=lambda t: t[0],
    )
    soon = [(d, c) for d, c in soon if d <= today + timedelta(days=args.days)]
    if not soon:
        print(f"No birthdays in the next {args.days} days.")
    for d, c in soon:
        channels = ", ".join(bot.platforms_for(c, cfg.channel_order)) or "no handle!"
        print(f"{d:%a %d %b}  {c.name:<30} via {channels}")
    return 0


def cmd_run(args, cfg: bot.Config) -> int:
    today = args.date or date.today()
    contacts = load_contacts(cfg.contacts_file)
    log = bot.run(cfg, contacts, today, dry_run=args.dry_run)
    print("\n".join(log) if log else f"No birthdays on {today}.")
    return 0


def cmd_daemon(args, cfg: bot.Config) -> int:
    hour, minute = map(int, args.at.split(":"))
    print(f"Birthday bot running; will check every day at {args.at}. Ctrl+C to stop.")
    while True:
        now = datetime.now()
        target = now.replace(hour=hour, minute=minute, second=0, microsecond=0)
        if target <= now:
            target += timedelta(days=1)
        time.sleep((target - now).total_seconds())
        try:
            cfg_now = bot.Config.load(args.config)  # pick up edits without restarting
            for line in bot.run(cfg_now, load_contacts(cfg_now.contacts_file), date.today()):
                print(line)
        except Exception as e:  # keep the daemon alive; report and retry tomorrow
            print(f"error during run: {e}", file=sys.stderr)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="birthday_bot", description="Automatic birthday wishes for your friends.")
    p.add_argument("-c", "--config", type=Path, default=Path("config.toml"), help="config file (default: config.toml)")
    sub = p.add_subparsers(dest="command", required=True)

    imp = sub.add_parser("import", help="import friends from a platform export")
    imp.add_argument("source", choices=sorted(IMPORTERS))
    imp.add_argument("file")
    imp.set_defaults(func=cmd_import)

    up = sub.add_parser("upcoming", help="list upcoming birthdays")
    up.add_argument("--days", type=int, default=30)
    up.add_argument("--date", type=_parse_date, help="pretend today is YYYY-MM-DD")
    up.set_defaults(func=cmd_upcoming)

    rn = sub.add_parser("run", help="send today's wishes (run once a day, e.g. from cron)")
    rn.add_argument("--dry-run", action="store_true", help="show what would be sent, send nothing")
    rn.add_argument("--date", type=_parse_date, help="pretend today is YYYY-MM-DD")
    rn.set_defaults(func=cmd_run)

    dm = sub.add_parser("daemon", help="stay running and send wishes every day at a set time")
    dm.add_argument("--at", default="09:00", help="local time HH:MM (default 09:00)")
    dm.set_defaults(func=cmd_daemon)

    args = p.parse_args(argv)
    cfg = bot.Config.load(args.config)
    return args.func(args, cfg)
