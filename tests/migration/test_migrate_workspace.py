"""Filtered tests for the one-off SQLite workspace export, using no third-party packages."""

import base64
from contextlib import closing
from decimal import Decimal
import hashlib
import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch


repository = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("migration", repository / "scripts/Migrate-SqliteWorkspace.py")
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)

PROJECT = "prj_migration"
CANDIDATE = "cand_migration"
RUN = "run_migration"
AT = "2026-10-01T12:00:00.0000000Z"
EXACT = Decimal("123456789.1234567890123456789")


def create_database(path, tables):
    path.parent.mkdir(parents=True, exist_ok=True)
    with closing(sqlite3.connect(path)) as database:
        for name, rows in tables.items():
            columns = list(rows[0])
            definitions = ", ".join(f'"{column}" ' + ("INTEGER" if isinstance(rows[0][column], int) else "TEXT") for column in columns)
            database.execute(f'CREATE TABLE "{name}" ({definitions})')
            placeholders = ", ".join("?" for _ in columns)
            database.executemany(f'INSERT INTO "{name}" VALUES ({placeholders})', [[row[column] for column in columns] for row in rows])
        database.commit()


def create_workspace(root):
    """All fields written by the version-2 store, including decimal JSON and typed identifiers."""
    artifact = "art_" + base64.urlsafe_b64encode(hashlib.sha256(b"strategy source").digest()).decode().rstrip("=")
    metrics = {
        "Gross": {"Profit": 100, "ReturnPercent": 2, "ProfitFactor": 2, "AverageTrade": 20},
        "Net": {"Profit": 80, "ReturnPercent": 1.6, "ProfitFactor": 1.5, "AverageTrade": 16},
        "Costs": {"Commission": 20, "Slippage": 0},
        "Risk": {"MaxDrawdownPercent": 1, "MaxDrawdownAmount": 30, "RecoveryFactor": 2},
        "Trades": {"Count": 5, "WinRatePercent": 60, "AverageHoldingMinutes": 10},
        "Activity": {"Turnover": 1000, "ExposurePercent": 5},
        "Concentration": {"LargestTradeProfitSharePercent": 30, "LargestSymbolProfitSharePercent": 100,
                          "LargestTradeId": "trade1", "LargestSymbol": "NVDA"},
        "ExecutionErrorCount": 0,
    }
    measurement = {
        "Candidate": CANDIDATE, "HeldOutSlice": 1, "Development": metrics,
        "HeldOut": metrics, "HeldOutStressed": metrics, "WalkForwardReturns": [EXACT, 2],
        "Silent": [], "Shape": {"Rules": 2, "Indicators": 1, "Parameters": 1}, "MeasuredAt": AT,
    }
    create_database(root / PROJECT / "odysseus.db", {
        "SchemaInfo": [{"Version": 2}],
        "Projects": [{"Id": PROJECT, "Name": "Migrated project", "Status": "Researching",
            "CreatedAt": AT, "UpdatedAt": AT, "Dataset": "ds_migration", "MaxBacktests": 60,
            "MaxCandidates": 40, "MaxWallClockTicks": 27000000000, "ClaimedBacktests": 3,
            "ClaimedCandidates": 1, "ClaimedWallClockTicks": 123456789}],
        "Candidates": [{"Sequence": 7, "Id": CANDIDATE, "Spec": "spec_migration", "Status": "Compiled",
            "ClassName": "ExampleStrategy", "SourceHash": "sourcehash", "AssemblyHash": "assemblyhash",
            "Source": artifact, "Assembly": artifact, "TranslatorVersion": "v1", "CreatedAt": AT, "UpdatedAt": AT}],
        "Runs": [{"Sequence": 9, "Id": RUN, "Candidate": CANDIDATE, "Dataset": "ds_migration",
            "Slice": "Development", "Window": 0, "Symbol": "NVDA", "Scenario": "baseline",
            "Fingerprint": "fingerprint", "Status": "Completed", "Metrics": json.dumps(metrics),
            "Trades": artifact, "Equity": artifact, "BarsProcessed": 100,
            "StartedAt": AT, "FinishedAt": AT, "Error": None, "Diagnosis": "retained diagnosis",
            "Parameters": '{"CapitalizedParameter": ' + str(EXACT) + '}'}],
        "Deployments": [{"Sequence": 4, "Id": "dep_migration", "Candidate": CANDIDATE, "Symbol": "NVDA",
            "Volume": str(EXACT), "Status": "Stopped", "StartedAt": AT, "StoppedAt": AT,
            "OrdersPlaced": 10, "Trades": 5, "RealizedProfit": "80.01", "Position": "0.0",
            "LastObservedAt": AT, "Note": "operator stopped", "Mode": "Paper", "ProcessId": 0, "SessionDays": 1}],
        "AuditEvents": [{"Sequence": 1, "Type": "ProjectCreated", "Actor": "Agent", "OccurredAt": AT,
            "PayloadHash": "hash", "Detail": "created"}],
        "Measurements": [{"Sequence": 8, "Candidate": CANDIDATE, "Content": migration.encode(measurement),
            "Runs": RUN, "MeasuredAt": AT}],
    })
    create_database(root / "operations.db", {"Operations": [
        {"Scope": "projects.create", "Key": "once", "Result": PROJECT, "RecordedAt": AT},
        {"Scope": "another", "Key": "once", "Result": "another result", "RecordedAt": AT},
    ]})
    create_database(root / "closed-history.db", {"Spent": [{"Sequence": 1, "Project": PROJECT,
        "Candidate": CANDIDATE, "Symbol": "NVDA", "TimeFrame": "00:05:00", "FromUtc": "2026-09-01T00:00:00Z",
        "ToUtc": "2026-10-01T00:00:00Z", "SpentAt": AT}]})
    artifact_path = root / PROJECT / "artifacts" / "existing-source.cs"
    artifact_path.parent.mkdir(parents=True)
    artifact_path.write_bytes(b"strategy source")


def read(path):
    return json.loads(path.read_text(encoding="utf-8"), parse_float=Decimal)


class MigrationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="odysseus-migration-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        create_workspace(self.root)

    def test_Migration_preserves_all_records_and_original_files(self):
        originals = {path: path.read_bytes() for path in self.root.rglob("*") if path.is_file()}
        self.assertEqual(8, migration.migrate(self.root))
        for path, original in originals.items():
            self.assertEqual(original, path.read_bytes())
        project = read(self.root / PROJECT / "project.json")
        self.assertEqual(3, project["budget"]["claimedBacktests"])
        self.assertEqual("00:00:12.3456789", project["budget"]["claimedWallClock"])
        run = read(next((self.root / PROJECT / "runs").glob("*.json")))
        self.assertEqual(EXACT, run["parameters"]["CapitalizedParameter"])
        self.assertEqual(80, run["metrics"]["net"]["profit"])
        self.assertEqual("retained diagnosis", run["diagnosis"])
        measurement = read(next((self.root / PROJECT / "measurements").glob("*.json")))
        self.assertEqual(EXACT, measurement["measurement"]["walkForwardReturns"][0])
        self.assertEqual([RUN], measurement["runs"])
        deployment = read(next((self.root / PROJECT / "deployments").glob("*.json")))
        self.assertEqual(EXACT, deployment["volume"])
        self.assertEqual(PROJECT, read(self.root / "operations.json")[0]["result"])
        self.assertEqual("2026-09-01T00:00:00Z", read(self.root / "closed-history.json")[0]["from"])

    def test_Migration_refuses_existing_metadata_before_writing_anything(self):
        path = self.root / "operations.json"
        path.write_text("existing data", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            migration.migrate(self.root)
        self.assertEqual("existing data", path.read_text(encoding="utf-8"))
        self.assertEqual([path], list(self.root.rglob("*.json")))

    def test_Migration_rejects_unknown_schema_before_writing(self):
        with closing(sqlite3.connect(self.root / PROJECT / "odysseus.db")) as database:
            database.execute("UPDATE SchemaInfo SET Version = 99")
            database.commit()
        with self.assertRaisesRegex(ValueError, "schema version 2"):
            migration.migrate(self.root)
        self.assertEqual([], list(self.root.rglob("*.json")))

    def test_Migration_removes_its_partial_export_on_write_failure(self):
        original_open = Path.open

        def fail_on_project(path, mode="r", *args, **kwargs):
            if path.name == "project.json" and mode == "x":
                raise OSError("simulated disk failure")
            return original_open(path, mode, *args, **kwargs)

        with patch.object(Path, "open", fail_on_project):
            with self.assertRaisesRegex(OSError, "simulated disk failure"):
                migration.migrate(self.root)
        self.assertEqual([], list(self.root.rglob("*.json")))


if __name__ == "__main__":
    unittest.main()
