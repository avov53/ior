# UI-only builds use an immutable local baseline, never cargo or llvm-strip.
function Resolve-UiNativeBaseline([string]$Root,[string]$Directory){
    $candidate=if([IO.Path]::IsPathRooted($Directory)){$Directory}else{Join-Path $Root $Directory}
    $resolved=(Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path
    if(!(Test-Path -LiteralPath (Join-Path $resolved 'native-hashes.json'))){throw 'Native baseline manifest is missing. Create and verify a backup before a UI-only build.'}
    return $resolved
}
function Copy-VerifiedUiNative([string]$Baseline,[string]$Platform,[string]$Destination){
    $manifest=Get-Content -LiteralPath (Join-Path $Baseline 'native-hashes.json') -Raw | ConvertFrom-Json
    $entries=@($manifest | Where-Object {$_.platform -eq $Platform})
    if(!$entries.Count){throw "No verified native dependencies for $Platform"}
    $targetRoot=[IO.Path]::GetFullPath($Destination).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    foreach($entry in $entries){
        $source=Join-Path $Baseline "dist/$Platform/$($entry.path)"
        $target=[IO.Path]::GetFullPath((Join-Path $Destination $entry.path))
        if(!$target.StartsWith($targetRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid native manifest path'}
        if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256){throw "Verified native baseline changed: $Platform/$($entry.path)"}
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.sha256){throw "Packaged native dependency changed: $Platform/$($entry.path)"}
    }
}
