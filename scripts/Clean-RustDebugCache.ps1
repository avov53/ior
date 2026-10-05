param(
    [Parameter(Mandatory = $true)]
    [string]$Root
)

$ErrorActionPreference = 'Stop'

try {
    $projectRoot = [System.IO.Path]::GetFullPath($Root)
    $projectRootItem = Get-Item -LiteralPath $projectRoot -Force
    if (-not $projectRootItem.PSIsContainer) {
        throw 'Project root is not a directory.'
    }

    $markers = @(
        (Join-Path $projectRoot 'dist.bat'),
        (Join-Path $projectRoot 'ior-core\Cargo.toml')
    )
    foreach ($marker in $markers) {
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw "Project marker is missing: $marker"
        }
    }

    $cachePath = Join-Path $projectRoot 'ior-core\target\debug'
    if (-not (Test-Path -LiteralPath $cachePath)) {
        Write-Host 'Rust debug cache is already absent.'
        exit 0
    }

    $targetPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'ior-core\target'))
    $cacheItem = Get-Item -LiteralPath $cachePath -Force
    if (-not $cacheItem.PSIsContainer -or ($cacheItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw 'Rust debug cache is not a normal directory; refusing to remove it.'
    }

    $resolvedCachePath = [System.IO.Path]::GetFullPath($cacheItem.FullName)
    if (-not [string]::Equals(
        $resolvedCachePath,
        (Join-Path $targetPath 'debug'),
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Resolved cache path is unexpected: $resolvedCachePath"
    }

    $sizeBytes = (Get-ChildItem -LiteralPath $resolvedCachePath -File -Recurse -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sizeBytes) { $sizeBytes = 0 }

    Remove-Item -LiteralPath $resolvedCachePath -Recurse -Force
    $sizeGiB = [math]::Round($sizeBytes / 1GB, 2)
    Write-Host "Removed Rust debug build cache ($sizeGiB GiB). Release artifacts were preserved."
    exit 0
}
catch {
    Write-Error "Rust debug cache cleanup failed: $_"
    exit 1
}
