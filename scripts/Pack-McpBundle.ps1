[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repository ('artifacts/mcp-distribution/' + $Version)
$stage = Join-Path $output 'bundle'
if (Test-Path -LiteralPath $stage) { throw "Bundle staging directory already exists: $stage" }
[IO.Directory]::CreateDirectory($stage) | Out-Null
$packages = Join-Path $output 'packages'
$arguments = @('pack', (Join-Path $repository 'src/Odysseus.Server/Odysseus.Server.csproj'), '-c', 'Release', '-o', $packages, ('-p:OdysseusVersion=' + $Version))
if ($NoRestore) { $arguments += '--no-restore' }
$nativePreference = $ErrorActionPreference
try {

    # Windows PowerShell treats native stderr as errors; the exit code decides whether packing succeeded.
    $ErrorActionPreference = 'Continue'
    & dotnet @arguments
    $packExitCode = $LASTEXITCODE
} finally { $ErrorActionPreference = $nativePreference }
if ($packExitCode -ne 0) { throw 'The MCP package could not be built.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Join-Path $packages ('StockSharp.Odysseus.Mcp.' + $Version + '.nupkg')
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in $archive.Entries) {
        $prefix = 'tools/net10.0/any/'
        if (-not $entry.FullName.StartsWith($prefix) -or $entry.FullName.EndsWith('/')) { continue }
        $relative = $entry.FullName.Substring($prefix.Length)
        $target = [IO.Path]::GetFullPath((Join-Path $stage ('server/' + $relative)))
        if (-not $target.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid package path: $relative" }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target)
    }
} finally { $archive.Dispose() }

$manifest = Get-Content -LiteralPath (Join-Path $repository 'distribution/claude/manifest.json') -Raw | ConvertFrom-Json
$manifest.version = $Version
if (-not (Test-Path -LiteralPath (Join-Path $stage $manifest.server.entry_point))) { throw 'The bundle is missing its MCP entry point.' }
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), (($manifest | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($false))
foreach ($name in @('README.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repository $name) -Destination (Join-Path $stage $name) }
$bundle = Join-Path $output ($manifest.name + '-' + $Version + '.mcpb')
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $bundle)
Write-Output "Created $bundle"
