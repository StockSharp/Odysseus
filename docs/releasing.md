# Releases and NuGet publishing

The release workflow builds one Odysseus version against one StockSharp source commit. It produces
Windows x64, Linux x64 and macOS arm64 archives, and two portable .NET tool packages:

| Package | Command |
|---|---|
| `StockSharp.Odysseus.Cli` | `odysseus` |
| `StockSharp.Odysseus.Mcp` | `odysseus-mcp` |

Both tools include their worker and runner in separate folders with each process's dependencies.
The tools use the child processes' DLL entry points, which work on every supported operating system.
The packages also carry the sample hypotheses, schemas, README and license.

## Checks before publication

`global.json` pins .NET SDK 10.0.400. The build workflow restores and builds the whole solution in
Release, runs the test assemblies sequentially with a filter, a two-minute hang timeout and a
fifteen-minute session timeout, then packs and installs the tools from a local feed. The CLI compiles a
candidate and backtests it through its packaged worker on generated data. The MCP tool completes an
initialize handshake. No broker account or product installer is configured for these probes.

A release calls that same build with its version and StockSharp commit. The resulting tool packages
are also installed and tried on Windows and macOS; Linux is checked by the build job. Each archive
is independently built and tried on its target operating system. Publication waits for all checks.

Only `OdysseusVersion` sets a release version. Setting the global `Version` property would also change
StockSharp assemblies, which broker connectors are compiled against.

## Trusted Publishing

Sign in to the personal NuGet.org profile that can publish for the StockSharp organization and create
a [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) policy:

- Package owner: `StockSharp`.
- Scopes: new packages and new package versions.
- Package glob: a pattern that includes both package IDs, such as `StockSharp.Odysseus.*`.
- GitHub repository owner: `StockSharp`.
- Repository: `Odysseus`.
- Workflow filename: `release.yml`.
- Environment: leave it unset; the workflow uses no GitHub environment.

Set the GitHub Actions repository variable `NUGET_USER` to that personal profile's NuGet username.
`NuGet/login@v1` exchanges the job's GitHub OIDC identity for a short-lived publishing key. Only the
authentication probe and publish job have `id-token: write`; no permanent API-key secret is required.

## Running the workflow

Open **Actions → release → Run workflow**, enter a version without `v`, and select a mode:

| `dry_run` | `verify_only` | Result |
|---|---|---|
| `true` | `true` | Verify the Trusted Publishing policy without building or publishing. |
| `true` | `false` | Verify authorization, build and test the tools and all three archives. |
| `false` | `false` | Run the full checks, upload both NuGet packages and create the GitHub release. |

Dry runs create no tag or release. A real run first uploads the packages, then creates `v<version>`
at the workflow's source commit and attaches the two packages and three archives. A prerelease version
such as `0.1.1-beta.1` is marked as a GitHub prerelease. Existing NuGet package versions are skipped on
retry; an existing GitHub release is never replaced.

Pushing a `v*` tag starts the same full publication automatically. Run a dry release first when changing
the packaging or workflow. NuGet.org may need a few minutes after upload before a new version can be
downloaded.
