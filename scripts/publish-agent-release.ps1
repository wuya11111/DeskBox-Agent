[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [ValidatePattern("^[0-9A-Za-z][0-9A-Za-z.-]*$")]
    [string]$Version = "1.4.8-agent.3",

    [string]$DotNetPath = "",

    [switch]$ReuseDeskBoxPublish,

    [switch]$SkipInstallers
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$artifactRoot = Join-Path $repoRoot ".artifacts\agent-release"
$runRoot = Join-Path $artifactRoot $runtimeIdentifier
$mcpBuildRoot = Join-Path $runRoot "mcp-build"
$mcpPublishDirectory = Join-Path $runRoot "mcp-publish"
$appPackageName = "DeskBox-Agent-$Version-App-$runtimeIdentifier"
$mcpPackageName = "DeskBox-Agent-$Version-MCP-$runtimeIdentifier"
$appPackageDirectory = Join-Path $runRoot $appPackageName
$mcpPackageDirectory = Join-Path $runRoot $mcpPackageName
$appZipPath = Join-Path $runRoot "$appPackageName.zip"
$mcpZipPath = Join-Path $runRoot "$mcpPackageName.zip"
$appInstallerPath = Join-Path $runRoot "$appPackageName.exe"
$mcpInstallerPath = Join-Path $runRoot "$mcpPackageName.exe"
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

function Get-InnoCompilerPath {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "ISCC.exe was not found. Install Inno Setup 6 or use -SkipInstallers."
}

function New-ChecksumFile {
    param([Parameter(Mandatory)][string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        "$Path.sha256",
        "$hash *$([System.IO.Path]::GetFileName($Path))$([Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))
    return $hash
}

function Add-ReleaseDocuments {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$ReadmePath
    )

    Copy-Item -LiteralPath $ReadmePath -Destination (Join-Path $Destination "README.md")
    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $Destination
    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE_CHANGE.md") -Destination $Destination
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
        "publish", $mcpProject,
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

$generatedPaths = @(
    $appPackageDirectory, $mcpPackageDirectory,
    $appZipPath, $mcpZipPath,
    "$appZipPath.sha256", "$mcpZipPath.sha256",
    $appInstallerPath, $mcpInstallerPath,
    "$appInstallerPath.sha256", "$mcpInstallerPath.sha256"
)
foreach ($path in $generatedPaths) {
    Assert-PathInsideRoot -Root $artifactRoot -Candidate $path
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $appPackageDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $deskBoxPublishDirectory "*") -Destination $appPackageDirectory -Recurse -Force
Add-ReleaseDocuments `
    -Destination $appPackageDirectory `
    -ReadmePath (Join-Path $repoRoot "docs\agent-app-release.md")

New-Item -ItemType Directory -Path $mcpPackageDirectory -Force | Out-Null
Copy-Item -LiteralPath $mcpExecutable -Destination $mcpPackageDirectory
Add-ReleaseDocuments `
    -Destination $mcpPackageDirectory `
    -ReadmePath (Join-Path $repoRoot "docs\agent-release.md")

if (Get-ChildItem -LiteralPath $appPackageDirectory -Filter "DeskBox.Mcp.exe" -File -Recurse) {
    throw "The DeskBox app-only package unexpectedly contains DeskBox.Mcp.exe."
}
if (Get-ChildItem -LiteralPath $mcpPackageDirectory -Filter "DeskBox.exe" -File -Recurse) {
    throw "The MCP-only package unexpectedly contains DeskBox.exe."
}

Compress-Archive -LiteralPath $appPackageDirectory -DestinationPath $appZipPath -CompressionLevel Optimal
Compress-Archive -LiteralPath $mcpPackageDirectory -DestinationPath $mcpZipPath -CompressionLevel Optimal
$appZipHash = New-ChecksumFile -Path $appZipPath
$mcpZipHash = New-ChecksumFile -Path $mcpZipPath

$appInstallerHash = $null
$mcpInstallerHash = $null
if (-not $SkipInstallers) {
    $innoCompiler = Get-InnoCompilerPath
    $appInstallerScript = if ($Platform -eq "ARM64") {
        Join-Path $repoRoot "installer\DeskBox.arm64.iss"
    }
    else {
        Join-Path $repoRoot "installer\DeskBox.iss"
    }
    $appInstallerName = [System.IO.Path]::GetFileNameWithoutExtension($appInstallerPath)
    $appInnoArguments = @(
        "/Qp",
        "/DDeskBoxNativeAot=1",
        "/DDeskBoxBundledRuntime=1",
        "/DDeskBoxIncludeMcp=0",
        "/DMyAppReleaseDir=$appPackageDirectory",
        "/F$appInstallerName",
        "/O$runRoot",
        $appInstallerScript
    )
    & $innoCompiler @appInnoArguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $appInstallerPath -PathType Leaf)) {
        throw "DeskBox app-only installer compilation failed."
    }

    if ($Platform -ne "x64") {
        throw "The standalone MCP installer currently supports x64 only."
    }
    $mcpInstallerName = [System.IO.Path]::GetFileNameWithoutExtension($mcpInstallerPath)
    $mcpInnoArguments = @(
        "/Qp",
        "/DMyAppVersion=$Version",
        "/DMcpReleaseDir=$mcpPackageDirectory",
        "/F$mcpInstallerName",
        "/O$runRoot",
        (Join-Path $repoRoot "installer\DeskBox.Mcp.iss")
    )
    & $innoCompiler @mcpInnoArguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $mcpInstallerPath -PathType Leaf)) {
        throw "DeskBox MCP-only installer compilation failed."
    }

    $appInstallerHash = New-ChecksumFile -Path $appInstallerPath
    $mcpInstallerHash = New-ChecksumFile -Path $mcpInstallerPath
}

$appFiles = @(Get-ChildItem -LiteralPath $appPackageDirectory -File -Recurse)
$mcpFiles = @(Get-ChildItem -LiteralPath $mcpPackageDirectory -File -Recurse)
[pscustomobject]@{
    Version = $Version
    Platform = $Platform
    RuntimeIdentifier = $runtimeIdentifier
    AppPackageFiles = $appFiles.Count
    AppPackageMiB = [Math]::Round(($appFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
    AppZipPath = $appZipPath
    AppZipMiB = [Math]::Round((Get-Item -LiteralPath $appZipPath).Length / 1MB, 1)
    AppZipSha256 = $appZipHash
    AppInstallerPath = if ($SkipInstallers) { $null } else { $appInstallerPath }
    AppInstallerSha256 = $appInstallerHash
    McpPackageFiles = $mcpFiles.Count
    McpPackageMiB = [Math]::Round(($mcpFiles | Measure-Object Length -Sum).Sum / 1MB, 1)
    McpZipPath = $mcpZipPath
    McpZipMiB = [Math]::Round((Get-Item -LiteralPath $mcpZipPath).Length / 1MB, 1)
    McpZipSha256 = $mcpZipHash
    McpInstallerPath = if ($SkipInstallers) { $null } else { $mcpInstallerPath }
    McpInstallerSha256 = $mcpInstallerHash
    McpToolCount = @($toolsResponse.result.tools).Count
}
