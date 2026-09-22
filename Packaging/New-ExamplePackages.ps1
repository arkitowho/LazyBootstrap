param([string] $OutputDirectory = (Join-Path $PSScriptRoot '../build/update-examples'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packageOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('LazyBootstrap-examples-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    foreach ($exampleName in @('copy', 'delete', 'mirror', 'editXml')) {
        $exampleSource = Join-Path $PSScriptRoot "examples/$exampleName"
        $packageDirectory = Join-Path $fixtureRoot $exampleName
        Copy-Item -LiteralPath $exampleSource -Destination $packageDirectory -Recurse
        & python (Join-Path $PSScriptRoot '../Tools/generate_update_checksums.py') $packageDirectory
        if ($LASTEXITCODE -ne 0) { throw "示例包校验清单生成失败：$exampleName" }
        $archiveDestination = Join-Path $packageOutput "UPDATE_LAZY_KFC_example_$exampleName.zip"
        Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archiveDestination -Force
    }
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedFixture)).StartsWith('LazyBootstrap-examples-', [StringComparison]::Ordinal)) {
        throw '示例包临时目录清理路径越界。'
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
Write-Host "示例包已生成：$packageOutput"
