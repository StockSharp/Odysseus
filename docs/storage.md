# Workspace storage

`ODYSSEUS_PROJECTS_ROOT` names the workspace owned by one MCP process. Research commands in the CLI use
the same storage; stop the MCP process before running them over that workspace. Concurrent tool calls
inside one process use the same store and are serialized during metadata changes. There is no
coordination between separate processes writing the same workspace.

```text
workspace/
  operations.json                    Scoped operation keys; first result wins
  closed-history.json                Spent closed stretches across all projects
  prj_<id>/
    project.json                     Name, status, dataset and persisted budget
    candidates/<sequence>-cand_<id>.json
    runs/<sequence>-run_<id>.json
    deployments/<sequence>-dep_<id>.json
    measurements/<sequence>.json      Measurement and its run identifiers
    audit/<sequence>.json
    specs/                           Specification revisions
    datasets/                        Frozen dataset manifests
    artifacts/                       Sources, assemblies and result series by content hash
    completed/                       Exported strategies, specifications and measurements
  runners/                           Each runner's plan, state and journal
```

Sequence numbers are padded to 20 digits and record insertion order, even when timestamps are equal
or a record is updated. There is no central project index: projects are discovered by their
`project.json`. JSON uses camel-case property names, string identifiers and string enums. Decimal
values are stored as JSON numbers without conversion to floating point. Metadata changes are written
to a temporary file beside the destination and moved into place after writing; unfinished `.tmp`
files are ignored when records are listed.

Market data is separate StockSharp file storage, configured by `ODYSSEUS_MARKET_DATA`, or is imported
through a connector. Neither bars nor strategy sources are embedded in these metadata records.

Runner claims still protect a project from starting another trading process while its previous
runner is alive. Runners only write their own homes; they do not share the MCP's project metadata.

## Migrating an existing SQLite workspace

Fresh workspaces need no migration. An older workspace containing `odysseus.db`, `operations.db` or
`closed-history.db` is refused until its data has been exported. Odysseus never silently starts an
empty project collection over those files.

1. Stop the MCP server and research CLI commands using the workspace. Make a backup of the whole
   workspace. A trading runner has its own files and does not use the databases being exported.
2. From this repository, run with Python 3, substituting the workspace folder:

   ```powershell
   python scripts/Migrate-SqliteWorkspace.py "path/to/workspace"
   ```

3. Start the new Odysseus over that folder. Project identifiers, budgets, operation keys, audit
   sequences, measurements and spent closed history remain intact. Existing specification, artifact,
   dataset and runner files are used in place.

The script uses Python's standard library, opens the databases read-only, and leaves them unchanged.
It supports schema version 2 used by Odysseus 1.0.1; open older schemas with that version first. It
refuses to overwrite existing JSON metadata. If an export was interrupted, rerun over an untouched
backup rather than mixing a partial export with new research. Keep the original databases as a
backup; after migration the new server reads only JSON and does not update the old database files.
