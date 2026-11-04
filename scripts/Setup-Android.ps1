$ErrorActionPreference='Stop'
$workspaceRoot=Split-Path $PSScriptRoot -Parent
$tools=Join-Path $workspaceRoot '.local/android-tools'
New-Item -ItemType Directory -Force (Join-Path $tools 'downloads') | Out-Null
function Fetch-Zip($url,$name,$destination,$checksum){
    $archive=Join-Path $tools "downloads/$name.zip"
    Invoke-WebRequest $url -OutFile $archive
    if((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $checksum.ToLowerInvariant()){throw "$name checksum mismatch"}
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $tools $destination) -Force
    Remove-Item -LiteralPath $archive
}
if(!(Test-Path (Join-Path $tools 'java'))){
    $asset=(Invoke-RestMethod 'https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=x64&heap_size=normal&image_type=jdk&jvm_impl=hotspot&os=windows&vendor=eclipse')[0].binary.package
    Fetch-Zip $asset.link 'jdk' 'java' $asset.checksum
}
if(!(Test-Path (Join-Path $tools 'commandline/cmdline-tools/bin/sdkmanager.bat'))){Fetch-Zip 'https://dl.google.com/android/repository/commandlinetools-win-15859902_latest.zip' 'commandline' 'commandline' '90ae805d20434428bffcb699c290860f19bb5f66a67e6b330067e3de801fb04a'}
if(!(Test-Path (Join-Path $tools 'gradle/gradle-8.13/bin/gradle.bat'))){Fetch-Zip 'https://services.gradle.org/distributions/gradle-8.13-bin.zip' 'gradle' 'gradle' (Invoke-RestMethod 'https://services.gradle.org/distributions/gradle-8.13-bin.zip.sha256').Trim()}
$env:JAVA_HOME=(Get-ChildItem (Join-Path $tools 'java') -Directory | Select-Object -First 1).FullName
$sdk=Join-Path $tools 'commandline/cmdline-tools/bin/sdkmanager.bat'
$sdkRoot=Join-Path $tools 'sdk'
1..80 | ForEach-Object {'y'} | & $sdk --sdk_root=$sdkRoot --licenses
& $sdk --sdk_root=$sdkRoot 'platforms;android-36' 'build-tools;36.0.0' 'platform-tools' 'ndk;27.2.12479018' 'cmake;3.22.1'
if($LASTEXITCODE -ne 0){throw 'SDK setup failed'}
& rustup target add aarch64-linux-android
if($LASTEXITCODE -ne 0){throw 'Rust Android target setup failed'}
