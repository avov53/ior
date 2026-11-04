param([switch]$Preflight,[string]$RunId,[string]$OutputDirectory='dist',[string]$Ref='')
$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$githubCommand=Get-Command gh.exe -CommandType Application -ErrorAction SilentlyContinue
if(!$githubCommand -and (Test-Path -LiteralPath "$env:ProgramFiles/GitHub CLI/gh.exe")){$githubCommand=Get-Item -LiteralPath "$env:ProgramFiles/GitHub CLI/gh.exe"; $githubCommand | Add-Member -NotePropertyName Source -NotePropertyValue $githubCommand.FullName}
if(!$githubCommand){throw 'GitHub CLI (gh.exe) not found. Install GitHub CLI, reopen the terminal, then run gh auth login.'}
$githubExecutable=$githubCommand.Source
function Invoke-GitHubCli([string[]]$Arguments) {
    $ErrorActionPreference='Continue'
    $result=& $githubExecutable @Arguments
    if($LASTEXITCODE -ne 0){throw 'GitHub command failed. Existing packages were preserved.'}
    return $result
}
Invoke-GitHubCli @('auth','status') | Out-Null
$remoteConfig=Join-Path (Get-Location) '.local/macos-remote.json'
$config=$null
if(Test-Path -LiteralPath $remoteConfig){
 $config=Get-Content -LiteralPath $remoteConfig -Encoding UTF8 -Raw | ConvertFrom-Json
 foreach($file in $config.files){$path=Join-Path (Get-Location) $file.path;if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.hash){throw "macOS input changed: $($file.path). Push updated sources before building."}}
}
$sha=if($config){$config.commit}else{(& git rev-parse HEAD).Trim()}
if($LASTEXITCODE -ne 0){throw 'Cannot determine source commit'}
if(!$RunId){
    if(!$config -and (& git status --porcelain --untracked-files=normal)){throw 'Commit and push source changes first. No files are uploaded automatically.'}
    if(!$Ref){$Ref=if($config){$config.ref}else{(& git branch --show-current).Trim()}}
    if(!$Ref){throw 'Choose a pushed branch using -Ref'}
    $remote=Invoke-GitHubCli @('api',"repos/avov53/ior/commits/$Ref",'--jq','.sha')
    if($remote.Trim() -ne $sha){throw 'Local and GitHub source commits differ.'}
    Invoke-GitHubCli @('api',"repos/avov53/ior/contents/.github/workflows/macos.yml?ref=$Ref",'--jq','.sha') | Out-Null
    if($Preflight){return}
    $request=[Guid]::NewGuid().ToString('N')
    $reuse=$false
    try {Invoke-GitHubCli @('workflow','run','macos.yml','--repo','avov53/ior','--ref',$Ref,'-f',"request_id=$request",'-f',"expected_sha=$sha") | Out-Null}
    catch {$reuse=$true;Write-Host 'Workflow is not on main yet; checking the branch push build.'}
    $deadline=(Get-Date).AddMinutes(3)
    do {
        $runs=Invoke-GitHubCli @('run','list','--repo','avov53/ior','--workflow','macos.yml','--limit','50','--json','databaseId,displayTitle,headSha,workflowName') | ConvertFrom-Json
        $run=$runs | Where-Object {($_.displayTitle -eq "macos-$request" -or ($reuse -and $_.workflowName -eq "macOS test package")) -and $_.headSha -eq $sha} | Select-Object -First 1
        if(!$run){Start-Sleep -Seconds 3}
    } while(!$run -and (Get-Date) -lt $deadline)
    if(!$run){throw 'Workflow was dispatched but not found. Inspect Actions and retry with -RunId.'}
    $RunId=[string]$run.databaseId
}
if($RunId -notmatch '^\d+$'){throw 'Invalid run ID'}
$info=Invoke-GitHubCli @('run','view',$RunId,'--repo','avov53/ior','--json','headSha,workflowName') | ConvertFrom-Json
if($info.headSha -ne $sha -or $info.workflowName -ne 'macOS test package'){throw 'Run does not match this source or workflow'}
Write-Host "Waiting for macOS build $RunId. Retry downloads using -RunId $RunId."
Invoke-GitHubCli @('run','watch',$RunId,'--repo','avov53/ior','--exit-status','--interval','10') | Out-Null
$root=(Resolve-Path '.').Path
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){[IO.Path]::GetFullPath($OutputDirectory)}else{[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))}
if(!$output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be within project'}
$stage=Join-Path $root ('.local/macos-download-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach($arch in @('arm64','x64')){
    $folder=Join-Path $stage "macos-$arch"
    Invoke-GitHubCli @('run','download',$RunId,'--repo','avov53/ior','--name',"ior-macos-$arch",'--dir',$folder) | Out-Null
    $manifest=Get-Content -LiteralPath "$folder/build.json" -Raw | ConvertFrom-Json
    if($manifest.commit -ne $sha -or $manifest.architecture -ne $arch){throw 'Artifact source mismatch'}
    $expected=(Get-Content -LiteralPath "$folder/SHA256SUMS.txt" -Raw).Split(' ')[0].Trim()
    if((Get-FileHash -LiteralPath "$folder/ior-macos-$arch.zip").Hash -ne $expected){throw 'Artifact checksum mismatch'}
}
New-Item -ItemType Directory -Force $output | Out-Null
foreach($arch in @('arm64','x64')){
    $destination=Join-Path $output "macos-$arch"
    if(Test-Path -LiteralPath $destination){Move-Item -LiteralPath $destination -Destination ($destination+'.previous-'+[Guid]::NewGuid().ToString('N'))}
    Move-Item -LiteralPath "$stage/macos-$arch" -Destination $destination
}
Write-Host "macOS packages downloaded to $output. See README before testing."
