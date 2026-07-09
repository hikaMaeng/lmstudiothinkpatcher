$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "LmStudioThinkPatcher.csproj"
$buildCache = Join-Path $root ".build-cache"

New-Item -ItemType Directory -Force -Path `
    (Join-Path $buildCache "dotnet-home"), `
    (Join-Path $buildCache "nuget-packages"), `
    (Join-Path $buildCache "nuget-http") | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $buildCache "dotnet-home"
$env:NUGET_PACKAGES = Join-Path $buildCache "nuget-packages"
$env:NUGET_HTTP_CACHE_PATH = Join-Path $buildCache "nuget-http"

try {
    dotnet publish $project -f net10.0-windows -c Release -r win-x64 --self-contained false
}
finally {
    foreach ($name in @("bin", "obj")) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

Write-Host "Windows app published to: $(Join-Path $root 'publish\windows')"
