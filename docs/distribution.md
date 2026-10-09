# MCP directories and Claude Desktop bundles

The MCP handshake name is `StockSharp.Odysseus`; the display name is `StockSharp Odysseus`.
The official MCP Registry identifier is `io.github.stocksharp/odysseus`.

## Official MCP Registry

`server.json` describes the NuGet tool and its local stdio transport. The `mcp-name` comment in the root
README is included in both NuGet tool packages and proves ownership of the MCP package to the Registry.

Publish NuGet version **1.0.1** through the existing `release.yml` workflow before publishing the
Registry record. Version 1.0.0 has no ownership marker and cannot be updated in place. Run the release
workflow with `version=1.0.1`, `dry_run=true`, `verify_only=false` first, then with `dry_run=false`.

From the repository root, authenticate and publish with the official Registry CLI:

```sh
mcp-publisher login github
mcp-publisher publish server.json
```

Use a GitHub account authorized to publish for the StockSharp organization. The account must grant the
Registry access to its organization memberships. See the
[Registry publishing guide](https://modelcontextprotocol.io/registry/quickstart).

## Claude Desktop

The bundle uses the same .NET tool files as the NuGet package, including the matching worker and
runner. It requires the **.NET 10 SDK on PATH**; the runtime and SDK are not installed by the bundle.

```powershell
./scripts/Pack-McpBundle.ps1 -Version 1.0.1
```

The result is `artifacts/mcp-distribution/1.0.1/stocksharp-odysseus-1.0.1.mcpb`.
Use `-NoRestore` only when the project dependencies have already been restored. The script refuses an
existing staging directory so files from an older package cannot enter a new bundle.

In Claude Desktop, open **Settings → Extensions → Advanced settings → Install Extension** and select
the bundle. Choose the projects folder and historical market data folder. Broker connections require
separate operator configuration; the extension does not configure credentials or enable product
installation.

After verifying the installed extension, submit it through the
[Anthropic desktop extension form](https://clau.de/desktop-extention-submission).
The form requires a Google sign-in. Include the bundle, repository, .NET 10 requirement and examples
from the README. Publication in the directory is subject to Anthropic review.

The public OpenAI plugin directory requires a hosted HTTPS MCP endpoint. This local bundle can be
used in desktop MCP clients; it does not turn Odysseus into a hosted service.

## Glama and Awesome MCP Servers

Submit the repository to [Glama](https://glama.ai/mcp/servers)
under **Runs from source**. Upload `distribution/glama/Dockerfile` directly to the listing's build
configuration when Glama requests it. The image installs published NuGet version 1.0.0, including its worker
and runner, with projects and market-data folders inside `/data`.

The published 1.0.0 tool reports `Odysseus.Server` as its handshake name. Version 1.0.1 carries the
explicit name and title listed at the top of this document.

Glama must build the image and pass its startup checks. Copy the score badge from the resulting
listing into the Awesome MCP Servers entry; that catalogue requires a verified Glama listing.
