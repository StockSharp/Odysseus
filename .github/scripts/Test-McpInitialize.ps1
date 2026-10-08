[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResponseJson,
    [Parameter(Mandatory)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$response = $ResponseJson | ConvertFrom-Json
if ($response.id -ne 1 -or -not $response.result.serverInfo -or $response.error) {
    throw 'The MCP server returned an incorrect initialize response.'
}

$reportedVersion = [string] $response.result.serverInfo.version
if ($reportedVersion.Split('+')[0] -cne $Version) {
    throw "The MCP server reports version '$reportedVersion', expected '$Version'."
}
Write-Output "The MCP server reports release version $Version."
