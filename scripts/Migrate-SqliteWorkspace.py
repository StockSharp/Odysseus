#!/usr/bin/env python3
"""One-off export of a stopped Odysseus SQLite workspace to plain JSON files.

Uses only Python's standard library. Original databases and existing artifacts are
never changed. Run from the repository: python scripts/Migrate-SqliteWorkspace.py ROOT
"""

import argparse
from contextlib import closing
from decimal import Decimal
import json
from pathlib import Path
import re
import sqlite3


def read_rows(path, table, order="Sequence"):
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as connection:
        connection.row_factory = sqlite3.Row
        return [dict(row) for row in connection.execute(f'SELECT * FROM "{table}" ORDER BY "{order}"')]


def camel(record):
    if isinstance(record, dict):
        return {key[:1].lower() + key[1:]: camel(value) for key, value in record.items()}
    if isinstance(record, list):
        return [camel(value) for value in record]
    return record


def content(text):
    return camel(json.loads(text, parse_float=Decimal)) if text is not None else None


def timespan(ticks):
    sign = "-" if ticks < 0 else ""
    seconds, fraction = divmod(abs(ticks), 10_000_000)
    days, seconds = divmod(seconds, 86_400)
    hours, seconds = divmod(seconds, 3_600)
    minutes, seconds = divmod(seconds, 60)
    return f"{sign}{str(days) + '.' if days else ''}{hours:02}:{minutes:02}:{seconds:02}.{fraction:07}"


def encode(value):
    # The default JSON encoder converts decimals to float. Financial values must
    # retain every digit and remain JSON numbers that System.Text.Json can read.
    if isinstance(value, Decimal):
        if not value.is_finite():
            raise ValueError("Non-finite decimal in legacy data")
        return str(value)
    if isinstance(value, dict):
        return "{" + ", ".join(json.dumps(key, ensure_ascii=False) + ": " + encode(item) for key, item in value.items()) + "}"
    if isinstance(value, list):
        return "[" + ", ".join(encode(item) for item in value) + "]"
    return json.dumps(value, ensure_ascii=False, allow_nan=False)


def record_path(folder, sequence, identifier=None):
    if identifier is not None and not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}", identifier):
        raise ValueError(f"Invalid identifier: {identifier}")
    suffix = "-" + identifier if identifier else ""
    return folder / f"{sequence:020}{suffix}.json"


def export_project(database, output):
    folder = database.parent
    versions = read_rows(database, "SchemaInfo", "Version")
    if len(versions) != 1 or versions[0]["Version"] != 2:
        raise ValueError(f"{database}: expected schema version 2; open older data with Odysseus 1.0.1 first")

    projects = read_rows(database, "Projects", "Id")
    if len(projects) != 1 or projects[0]["Id"] != folder.name:
        raise ValueError(f"{database}: the project does not match its folder")
    project = camel(projects[0])
    project["budget"] = {
        "maxBacktests": project.pop("maxBacktests"),
        "maxCandidates": project.pop("maxCandidates"),
        "maxWallClock": timespan(project.pop("maxWallClockTicks")),
        "claimedBacktests": project.pop("claimedBacktests"),
        "claimedCandidates": project.pop("claimedCandidates"),
        "claimedWallClock": timespan(project.pop("claimedWallClockTicks")),
    }
    output[folder / "project.json"] = project

    for table, directory in (("Candidates", "candidates"), ("Runs", "runs"), ("Deployments", "deployments")):
        for row in read_rows(database, table):
            sequence = row.pop("Sequence")
            record = camel(row)
            if table == "Runs":
                record["metrics"] = content(row["Metrics"])
                record["parameters"] = json.loads(row["Parameters"] or "{}", parse_float=Decimal)
            elif table == "Deployments":
                for key in ("volume", "realizedProfit", "position"):
                    record[key] = Decimal(record[key])
            output[record_path(folder / directory, sequence, row["Id"])] = record

    for row in read_rows(database, "AuditEvents"):
        output[record_path(folder / "audit", row["Sequence"])] = camel(row)
    for row in read_rows(database, "Measurements"):
        output[record_path(folder / "measurements", row["Sequence"])] = {
            "measurement": content(row["Content"]),
            "runs": row["Runs"].split(",") if row["Runs"] else [],
        }


def migrate(root):
    root = root.resolve(strict=True)
    output = {}
    for database in sorted(root.glob("*/odysseus.db")):
        export_project(database, output)

    operations = root / "operations.db"
    if operations.exists():
        output[root / "operations.json"] = [camel(row) for row in read_rows(operations, "Operations", "rowid")]
    history = root / "closed-history.db"
    if history.exists():
        entries = []
        for row in read_rows(history, "Spent"):
            row.pop("Sequence")
            row["From"] = row.pop("FromUtc")
            row["To"] = row.pop("ToUtc")
            entries.append(camel(row))
        output[root / "closed-history.json"] = entries

    if not output:
        raise ValueError(f"No legacy databases found in {root}")
    for path in output:
        if path.exists():
            raise FileExistsError(f"Refusing to overwrite {path}; use an untouched copy of the legacy workspace")
    encoded = {path: encode(value) + "\n" for path, value in output.items()}

    written = []
    try:
        # Project metadata is the last part published: its presence is how the
        # new store knows the rest of this project's records have been exported.
        for path, text in sorted(encoded.items(), key=lambda item: (item[0].name == "project.json", str(item[0]))):
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("x", encoding="utf-8") as destination:
                written.append(path)
                destination.write(text)
    except BaseException:
        for path in reversed(written):
            path.unlink()
        raise
    return len(written)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path, help="Stopped Odysseus workspace (ODYSSEUS_PROJECTS_ROOT)")
    args = parser.parse_args()
    try:
        count = migrate(args.root)
    except (OSError, ValueError, sqlite3.Error, KeyError) as error:
        parser.exit(1, f"Migration failed: {error}\n")
    print(f"Exported {count} JSON files. Original databases were kept unchanged.")
