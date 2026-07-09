$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "LmStudioThinkPatcher.csproj"

try {
    dotnet publish $project -c Release -r win-x64 --self-contained false
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
