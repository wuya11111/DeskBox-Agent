[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [ValidatePattern("^[0-9A-Za-z][0-9A-Za-z.-]*$")]
    [string]$Version = "1.4.8-agent.1",

    [string]$DotNetPath = "",

    [switch]$ReuseDeskBoxPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$artifactRoot = Join-Path $repoRoot ".artifacts\agent-release"
$runRoot = Join-Path $artifactRoot $runtimeIdentifier
$mcpBuildRoot = Join-Path $runRoot "mcp-build"
$mcpPublishDirectory = Join-Path $runRoot "mcp-publish"
$packageName = "DeskBox-Agent-$Version-$runtimeIdentifier"
$packageDirectory = Join-Path $runRoot $packageName
$zipPath = Join-Path $runRoot "$packageName.zip"
$checksumPath = "$zipPath.sha256"
$deskBoxPublishDirectory = Join-Path $repoRoot ".artifacts\aot-retail\$runtimeIdentifier\publish"
$mcpProject = Join-Path $repoRoot "src\DeskBox.Mcp\DeskBox.Mcp.csproj"
$toolchainScript = Join-Path $PSScriptRoot "rust-arm64-msvc-environment.ps1"

function Assert-PathInsideRoot {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate)
    if (-not $normalizedCandidate.StartsWith(
            $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a release path outside '$normalizedRoot': '$normalizedCandidate'."
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "'$Path' is not a PE image."
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "'$Path' does not contain a PE signature."
        }
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$dotnet = if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    (Get-Command dotnet -ErrorAction Stop).Source
}
else {
    $candidate = [System.IO.Path]::GetFullPath($DotNetPath)
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "The explicitly selected dotnet host does not exist: '$candidate'."
    }
    $candidate
}

if (-not $ReuseDeskBoxPublish) {
    $publishParameters = @{ Platform = $Platform }
    if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
        $publishParameters.DotNetPath = $dotnet
    }
    & (Join-Path $PSScriptRoot "publish-aot-retail.ps1") @publishParameters
}

if (-not (Test-Path -LiteralPath (Join-Path $deskBoxPublishDirectory "DeskBox.exe") -PathType Leaf)) {
    throw "DeskBox retail output was not found at '$deskBoxPublishDirectory'."
}

. $toolchainScript
$toolchain = Get-DeskBoxMsvcEnvironment -Platform $Platform
$environmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $toolchain
try {
    foreach ($path in @($mcpBuildRoot, $mcpPublishDirectory)) {
        Assert-PathInsideRoot -Root $artifactRoot -Candidate $path
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    $mcpPublishArguments = @(
        "publish",
        $mcpProject,
        "--configuration", "Release",
        "--runtime", $runtimeIdentifier,
        "--self-contained", "true",
        "--artifacts-path", $mcpBuildRoot,
        "--output", $mcpPublishDirectory,
        "-p:PublishAot=true",
        "-p:InvariantGlobalization=true",
        "-p:IlcUseEnvironmentalTools=true",
        "-p:NoWarn=IL2026%3BIL3050",
        "-v:minimal"
    )
    & $dotnet @mcpPublishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "DeskBox MCP Native AOT publish failed."
    }
}
finally {
    Exit-DeskBoxMsvcEnvironment -State $environmentState
}

$mcpExecutable = Join-Path $mcpPublishDirectory "DeskBox.Mcp.exe"
if (-not (Test-Path -LiteralPath $mcpExecutable -PathType Leaf)) {
    throw "DeskBox MCP output is missing '$mcpExecutable'."
}
$expectedMachine = if ($Platform -eq "ARM64") { 0xAA64 } else { 0x8664 }
if ((Get-PeMachine -Path $mcpExecutable) -ne $expectedMachine) {
    throw "DeskBox MCP executable architecture does not match $Platform."
}

$initializeRequest = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
$initializeResponse = $initializeRequest | & $mcpExecutable | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $initializeResponse.result.serverInfo.name -ne "deskbox") {
    throw "DeskBox MCP initialize smoke test failed."
}
$toolsRequest = '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
$toolsResponse = $toolsRequest | & $mcpExecutable | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or @($toolsResponse.result.tools).Count -eq 0) {
    throw "DeskBox MCP tools/list smoke test failed."
}

foreach ($path in @($packageDirectory, $zipPath, $checksumPath)) {
    Assert-PathInsideRoot -Root $artifactRoot -Candidate $path
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $deskBoxPublishDirectory "*") -Destination $packageDirectory -Recurse -Force
$packageMcpDirectory = Join-Path $packageDirectory "mcp"
New-Item -ItemType Directory -Path $packageMcpDirectory -Force | Out-Null
Copy-Item -LiteralPath $mcpExecutable -Destination $packageMcpDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot "docs\agent-release.md") `
    -Destination (Join-Path $packageDirectory "README.md")
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE_CHANGE.md") -Destination $packageDirectory

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(
    $checksumPath,
    "$zipHash *$([System.IO.Path]::GetFileName($zipPath))$([Environment]::NewLine)",
    [System.Text.UTF8Encoding]::new($false))

$packageFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse)
[pscustomobject]@{
    Version = $Version
    Platform = $Platform
    RuntimeIdentifier = $runtimeIdentifier
    PackageDirectory = $packageDirectory
    PackageFiles = $packageFiles.Count
    PackageMiB = [Math]::Round(
        ($packageFiles | Measure-Object -Property Length -Sum).Sum / 1MB,
        1)
    ZipPath = $zipPath
    ZipMiB = [Math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Sha256 = $zipHash
    McpToolCount = @($toolsResponse.result.tools).Count
}
