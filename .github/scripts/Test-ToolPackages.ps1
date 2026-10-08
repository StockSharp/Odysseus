[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,
    [Parameter(Mandatory)]
    [string] $Version,
    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$expected = @{
    'StockSharp.Odysseus.Cli' = @{ Command = 'odysseus'; Assembly = 'odysseus.dll' }
    'StockSharp.Odysseus.Mcp' = @{ Command = 'odysseus-mcp'; Assembly = 'Odysseus.Server.dll' }
}
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File)
if ($packages.Count -ne $expected.Count) { throw "Expected two tool packages, found $($packages.Count)." }
$seen = @()
foreach ($package in $packages) {
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $specs = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($specs.Count -ne 1) { throw "$($package.Name) must contain one nuspec." }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $spec.package.metadata
        $id = [string] $metadata.id
        if (-not $expected.ContainsKey($id) -or $seen -contains $id) { throw "Unexpected or repeated package $id." }
        $seen += $id
        if ($metadata.version -ne $Version) { throw "$id has version $($metadata.version), expected $Version." }
        if ($metadata.repository.url -ne 'https://github.com/StockSharp/Odysseus') { throw "$id has an incorrect source repository." }
        if (@($metadata.packageTypes.packageType.name) -notcontains 'DotnetTool') { throw "$id is not a .NET tool." }
        $root = 'tools/net10.0/any/'
        $required = @(
            'LICENSE', 'README.md', ($root + 'DotnetToolSettings.xml'), ($root + $expected[$id].Assembly),
            ($root + 'worker/Odysseus.Worker.dll'), ($root + 'worker/Odysseus.Worker.deps.json'),
            ($root + 'worker/Odysseus.Worker.runtimeconfig.json'), ($root + 'runner/Odysseus.Runner.dll'),
            ($root + 'runner/Odysseus.Runner.deps.json'), ($root + 'runner/Odysseus.Runner.runtimeconfig.json'),
            ($root + 'samples/hypothesis.json'), ($root + 'schemas/strategy-spec.schema.json')
        )
        foreach ($path in $required) {
            if (-not $archive.GetEntry($path)) { throw "$id does not contain $path." }
        }
        foreach ($apphost in @('worker/Odysseus.Worker', 'worker/Odysseus.Worker.exe', 'runner/Odysseus.Runner', 'runner/Odysseus.Runner.exe')) {
            if ($archive.GetEntry($root + $apphost)) { throw "$id contains a platform-specific process apphost." }
        }
        $reader = [IO.StreamReader]::new($archive.GetEntry($root + 'DotnetToolSettings.xml').Open())
        try { [xml] $settings = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $command = $settings.DotNetCliTool.Commands.Command
        if ($command.Name -ne $expected[$id].Command -or $command.EntryPoint -ne $expected[$id].Assembly -or $command.Runner -ne 'dotnet') {
            throw "$id has an incorrect tool entry point."
        }
    } finally { $archive.Dispose() }
}
Write-Output "Validated two Odysseus tool packages at version $Version."
if ($ValidateOnly) { return }

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$probeRoot = Join-Path $repository ('artifacts/tool-tests/' + [Guid]::NewGuid().ToString('n'))
[IO.Directory]::CreateDirectory($probeRoot) | Out-Null
$nugetConfig = Join-Path $probeRoot 'NuGet.Config'
$source = [Security.SecurityElement]::Escape($PackageDirectory)
[IO.File]::WriteAllText($nugetConfig, "<configuration><packageSources><clear/><add key=`"local`" value=`"$source`"/></packageSources></configuration>")

function New-ProbeProcess {
    param([string] $File, [string[]] $CommandArguments)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File
    $start.WorkingDirectory = $probeRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($start.PSObject.Properties.Name -contains 'ArgumentList') {
        foreach ($argument in $CommandArguments) { $start.ArgumentList.Add($argument) }
    } else {
        $start.Arguments = ($CommandArguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    }
    foreach ($name in @($start.EnvironmentVariables.Keys)) {
        if ($name.StartsWith('ODYSSEUS_', [StringComparison]::OrdinalIgnoreCase)) { $start.EnvironmentVariables.Remove($name) }
    }
    $start.EnvironmentVariables['ODYSSEUS_PROJECTS_ROOT'] = Join-Path $probeRoot 'projects'
    $start.EnvironmentVariables['ODYSSEUS_MARKET_DATA'] = Join-Path $probeRoot 'market-data'
    $start.EnvironmentVariables['NUGET_PACKAGES'] = Join-Path $probeRoot 'nuget-cache'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    return $process
}

function Invoke-Probe {
    param([string] $File, [string[]] $CommandArguments, [int] $TimeoutSeconds = 120)
    $process = New-ProbeProcess $File $CommandArguments
    try {
        $process.Start() | Out-Null
        $process.StandardInput.Close()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            throw "Timed out: $File $($CommandArguments -join ' ')"
        }
        $stdout = $output.GetAwaiter().GetResult()
        $stderr = $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "$File exited $($process.ExitCode).`n$stdout`n$stderr" }
        if ($stdout) { Write-Output $stdout.TrimEnd() }
    } finally { $process.Dispose() }
}

foreach ($id in $expected.Keys) {
    $toolDirectory = Join-Path $probeRoot $id
    Invoke-Probe 'dotnet' @('tool', 'install', $id, '--version', $Version, '--tool-path', $toolDirectory, '--configfile', $nugetConfig, '--no-cache')
}
$extension = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { '.exe' } else { '' }
$cliDirectory = Join-Path $probeRoot 'StockSharp.Odysseus.Cli'
$cli = Join-Path $cliDirectory ('odysseus' + $extension)
$entry = @(Get-ChildItem -LiteralPath $cliDirectory -Filter 'odysseus.dll' -File -Recurse -Force)
if ($entry.Count -ne 1) { throw 'The installed CLI entry point is missing or ambiguous.' }
$hypothesis = Join-Path $entry[0].DirectoryName 'samples/hypothesis.json'
Invoke-Probe $cli @('new', 'package check')
Invoke-Probe $cli @('demo')
Invoke-Probe $cli @('propose', $hypothesis)
Invoke-Probe $cli @('build')
Invoke-Probe $cli @('backtest')
Write-Output 'The installed CLI compiled a candidate and backtested it through its bundled worker.'

$server = Join-Path (Join-Path $probeRoot 'StockSharp.Odysseus.Mcp') ('odysseus-mcp' + $extension)
$process = New-ProbeProcess $server @()
$started = $false
try {
    $process.Start() | Out-Null
    $started = $true
    $errorOutput = $process.StandardError.ReadToEndAsync()
    $hello = @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'package-check'; version = '1' } } } | ConvertTo-Json -Depth 4 -Compress
    $process.StandardInput.WriteLine($hello)
    $answer = $process.StandardOutput.ReadLineAsync()
    if (-not $answer.Wait(30000)) { throw 'The installed MCP server did not answer initialize in 30 seconds.' }
    $response = $answer.GetAwaiter().GetResult() | ConvertFrom-Json
    if ($response.id -ne 1 -or -not $response.result.serverInfo -or $response.error) { throw 'The installed MCP server returned an incorrect initialize response.' }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(30000)) { throw 'The installed MCP server did not exit after its input closed.' }
    if ($process.ExitCode -ne 0) { throw "The MCP server exited $($process.ExitCode): $($errorOutput.GetAwaiter().GetResult())" }
    Write-Output 'The installed MCP server completed initialize and shut down cleanly.'
} finally {
    if ($started -and -not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}
