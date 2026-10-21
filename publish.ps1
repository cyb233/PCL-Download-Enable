[CmdletBinding()]
param(
    [string]$OutputPath = "publish"
)

$ErrorActionPreference = "Stop"

if (Test-Path -LiteralPath $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath | Out-Null

$selfContainedPath = Join-Path $OutputPath ".self-contained"
$frameworkDependentPath = Join-Path $OutputPath ".framework-dependent"

$common = @(
    "PclDownloadEnable.csproj",
    "--configuration", "Release",
    "-p:PublishSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

dotnet publish @common --runtime win-x86 --output $selfContainedPath -p:SelfContained=true -p:AssemblyName=PCL-Download-Enable -p:EnableCompressionInSingleFile=true

dotnet publish @common --runtime win-x64 --output $frameworkDependentPath -p:SelfContained=false -p:PlatformTarget=x64 -p:AssemblyName=PCL-Download-Enable.Net8 -p:EnableCompressionInSingleFile=false

Copy-Item (Join-Path $selfContainedPath "PCL-Download-Enable.exe") (Join-Path $OutputPath "PCL-Download-Enable.exe")
Copy-Item (Join-Path $frameworkDependentPath "PCL-Download-Enable.Net8.exe") (Join-Path $OutputPath "PCL-Download-Enable.Net8.exe")
Remove-Item $selfContainedPath, $frameworkDependentPath -Recurse -Force
