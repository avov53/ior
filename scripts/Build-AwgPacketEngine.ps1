param([string]$GoPath='go',[switch]$VerifyOnly)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binary=Join-Path $repo 'core\ior-core\ior-awg-adapter.exe'
$expected='9a39a3f98ab96af22c58727945759d2d5faf8db5792083ea49214214cc0abe85'
if(-not $VerifyOnly){
    if((& $GoPath version) -notmatch 'go1\.27\.1 windows/amd64'){throw 'Pinned Go 1.27.1 Windows amd64 required.'}
    Push-Location -LiteralPath (Join-Path $repo 'ior-core\adapters\amneziawg')
    $staged=$binary+'.next'
    try {& $GoPath test ./...;if($LASTEXITCODE -ne 0){throw 'AWG tests failed.'}; & $GoPath build -buildvcs=false -trimpath -ldflags='-s -w' -o $staged .;if($LASTEXITCODE -ne 0){throw 'AWG build failed.'}}
    finally {Pop-Location}
    if((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw 'Staged AWG build SHA-256 mismatch; existing engine preserved.'}
    Copy-Item -LiteralPath $staged -Destination $binary -Force
    Remove-Item -LiteralPath $staged
}
if(-not (Test-Path -LiteralPath $binary) -or (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw 'AWG packet engine missing or SHA-256 mismatch.'}
Write-Host 'Pinned AWG 3.1 packet engine verified.'
