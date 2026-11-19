param([switch]$SkipNative, [switch]$Debug, [string]$OutputDirectory='android-output/current',
    [switch]$ReuseVerifiedNative, [switch]$NewInterface, [switch]$RunUiChecks, [string]$BaselineDirectory='.local/backups/ui-integration-20261117')
$ErrorActionPreference='Stop'
function Invoke-AndroidBuildTool([string]$Tool,[string[]]$Arguments,[string]$Log='') {
    # Windows PowerShell 5 treats normal native stderr (JAVA_TOOL_OPTIONS,
    # compiler warnings) as errors. The process exit code decides success.
    $ErrorActionPreference='Continue'
    if($Log){& $Tool @Arguments *> $Log}else{& $Tool @Arguments}
    $code=$LASTEXITCODE
    if($code -ne 0){if($Log){Get-Content -LiteralPath $Log -Tail 35};throw "Android build tool failed ($code): $Tool"}
}
$workspaceRoot=Split-Path $PSScriptRoot -Parent
if($ReuseVerifiedNative){
    . "$PSScriptRoot/Verified-UiNative.ps1"
    $baseline=Resolve-UiNativeBaseline $workspaceRoot $BaselineDirectory
    $nativeManifest=Get-Content -LiteralPath "$baseline/native-hashes.json" -Raw | ConvertFrom-Json
    $nativeEntries=@($nativeManifest | Where-Object platform -eq android)
    $nativeEntry=@($nativeEntries | Where-Object path -eq 'lib/arm64-v8a/libior_android.so')
    if($nativeEntry.Count -ne 1){throw 'Expected one verified Android core library'}
    $verifiedNative=Join-Path $baseline 'native/android/libior_android.so'
    if((Get-FileHash -LiteralPath $verifiedNative -Algorithm SHA256).Hash -ne $nativeEntry[0].sha256){throw 'Android native baseline changed'}
    $SkipNative=$true
}
$tools=Join-Path $workspaceRoot '.local/android-tools'
$java=Get-ChildItem (Join-Path $tools 'java') -Directory | Select-Object -First 1
if (!$java) { throw 'Run scripts/Setup-Android.ps1 first.' }
$env:JAVA_HOME=$java.FullName
$env:ANDROID_HOME=Join-Path $tools 'sdk'
$env:ANDROID_USER_HOME=Join-Path $tools 'android-user'
New-Item -ItemType Directory -Force $env:ANDROID_USER_HOME | Out-Null
$env:GRADLE_USER_HOME=Join-Path $tools 'gradle-home'
New-Item -ItemType Directory -Force $env:GRADLE_USER_HOME | Out-Null
$env:GRADLE_USER_HOME=& cmd.exe /d /c "for %I in (`"$env:GRADLE_USER_HOME`") do @echo %~sI"
$buildTemp=Join-Path $tools 'tmp'
New-Item -ItemType Directory -Force $buildTemp | Out-Null
$shortTemp=& cmd.exe /d /c "for %I in (`"$buildTemp`") do @echo %~sI"
$env:JAVA_TOOL_OPTIONS="-Dfile.encoding=UTF-8 -Djdk.net.unixdomain.tmpdir=$shortTemp -Djava.io.tmpdir=$shortTemp"
$gradle=Join-Path $tools 'gradle/gradle-8.13/bin/gradle.bat'
$ndk=Join-Path $env:ANDROID_HOME 'ndk/27.2.12479018'
$compiler=Join-Path $ndk 'toolchains/llvm/prebuilt/windows-x86_64/bin'
$env:PATH=(Join-Path $env:JAVA_HOME 'bin')+';'+(Join-Path $env:ANDROID_HOME 'cmake/3.22.1/bin')+';'+$compiler+';'+$env:PATH
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){[IO.Path]::GetFullPath($OutputDirectory)}else{[IO.Path]::GetFullPath((Join-Path $workspaceRoot $OutputDirectory))}
if($NewInterface -and (Test-Path -LiteralPath (Join-Path $output 'ior-android-arm64.apk'))){throw 'Use a new output directory to preserve the previous UI test APK'}
New-Item -ItemType Directory -Force $output | Out-Null
Push-Location $workspaceRoot
try {
    if($NewInterface){
        if($SkipNative -and !$ReuseVerifiedNative){throw 'New interface builds require a fresh or verified Android core'}
        & node design/app/build.mjs android
        if($LASTEXITCODE -ne 0){throw 'New Android interface build failed'}
        $assetStage=Join-Path $output 'assets'
        New-Item -ItemType Directory -Force $assetStage | Out-Null
        $oldChunks=Join-Path $assetStage 'assets'
        $newChunks=Join-Path $workspaceRoot 'design/app/out/android/assets'
        if(Test-Path -LiteralPath $oldChunks){
            $expected=@(Get-ChildItem -LiteralPath $newChunks -File | Select-Object -ExpandProperty Name)
            $stageRoot=[IO.Path]::GetFullPath($oldChunks).TrimEnd('\')+'\'
            Get-ChildItem -LiteralPath $oldChunks -File | Where-Object {$_.Name -match '^(index|browser)-[A-Za-z0-9_-]+\.(js|css)$' -and $_.Name -notin $expected} | ForEach-Object {
                if(!$_.FullName.StartsWith($stageRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid UI staging path'}
                Remove-Item -LiteralPath $_.FullName
            }
        }
        Copy-Item -Path "$workspaceRoot/android/app/src/main/assets/*" -Destination $assetStage -Recurse -Force
        Copy-Item -Path "$workspaceRoot/design/app/out/android/*" -Destination $assetStage -Recurse -Force
        $env:IOR_ANDROID_UI_ASSETS=$assetStage
    }else{
        & node android/build-ui.mjs
        if($LASTEXITCODE -ne 0){throw 'Android interface build failed'}
    }
    if($NewInterface){
        & node android/build-geodata.mjs
        if($LASTEXITCODE -ne 0){throw 'Routing geodata build failed'}
    }
    if (!$SkipNative) {
        $env:CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER=Join-Path $compiler 'aarch64-linux-android29-clang.cmd'
        $env:CC_aarch64_linux_android=$env:CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER
        $env:CXX_aarch64_linux_android=Join-Path $compiler 'aarch64-linux-android29-clang++.cmd'
        $env:AR_aarch64_linux_android=Join-Path $compiler 'llvm-ar.exe'
        $env:CMAKE_TOOLCHAIN_FILE=Join-Path $ndk 'build/cmake/android.toolchain.cmake'
        $env:ANDROID_NDK=$ndk
        $env:ANDROID_PLATFORM='android-29'
        $env:ANDROID_ABI='arm64-v8a'
        $env:CARGO_TARGET_AARCH64_LINUX_ANDROID_RUSTFLAGS='-C link-arg=-Wl,-z,max-page-size=16384'
        Push-Location ior-core
        try { Invoke-AndroidBuildTool 'cargo' @('build','-p','ior-android','--release','--locked','--target','aarch64-linux-android') (Join-Path $output 'rust-build.log') } finally { Pop-Location }
    }
    $native=if($ReuseVerifiedNative){$verifiedNative}else{Join-Path $workspaceRoot 'ior-core/target/aarch64-linux-android/release/libior_android.so'}
    if(!(Test-Path -LiteralPath $native)){throw 'Native ior-core library missing'}
    $jni=if($ReuseVerifiedNative){Join-Path $output 'jniLibs/arm64-v8a'}else{Join-Path $workspaceRoot 'android/app/src/main/jniLibs/arm64-v8a'}
    if($ReuseVerifiedNative){$env:IOR_ANDROID_UI_JNI=Split-Path $jni -Parent}
    New-Item -ItemType Directory -Force $jni | Out-Null
    Copy-Item -LiteralPath $native -Destination (Join-Path $jni 'libior_android.so')
    if(!$ReuseVerifiedNative){
        & (Join-Path $compiler 'llvm-strip.exe') --strip-unneeded (Join-Path $jni 'libior_android.so')
        if($LASTEXITCODE -ne 0){throw 'Native strip failed'}
    }
    if(!$Debug) {
        $keys=Join-Path $workspaceRoot '.local/android-signing'
        if($ReuseVerifiedNative -and (!(Test-Path -LiteralPath "$keys/signing.json") -or !(Test-Path -LiteralPath "$keys/ior-release.jks"))){throw 'UI-only builds require the existing Android signing key; creating a different key is prohibited.'}
        New-Item -ItemType Directory -Force $keys | Out-Null
        $secrets=Join-Path $keys 'signing.json'
        if(!(Test-Path -LiteralPath $secrets)){
            $password=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
            [ordered]@{password=$password} | ConvertTo-Json | Set-Content -LiteralPath $secrets -Encoding utf8
            $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            & icacls.exe $keys /inheritance:r /grant:r "*$($sid):(OI)(CI)F" | Out-Null
            if($LASTEXITCODE -ne 0){throw 'Signing key directory permissions failed'}
        }
        $env:IOR_ANDROID_KEY_PASSWORD=(Get-Content -LiteralPath $secrets -Raw | ConvertFrom-Json).password
        $env:IOR_ANDROID_KEYSTORE=Join-Path $keys 'ior-release.jks'
        if(!(Test-Path -LiteralPath $env:IOR_ANDROID_KEYSTORE)) {
            & (Join-Path $env:JAVA_HOME 'bin/keytool.exe') -genkeypair -keystore $env:IOR_ANDROID_KEYSTORE -storepass:env IOR_ANDROID_KEY_PASSWORD -keypass:env IOR_ANDROID_KEY_PASSWORD -alias ior -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=ior Android, O=ior'
            if($LASTEXITCODE -ne 0){throw 'APK signing key generation failed'}
        }
    }
    $task=if($Debug){'assembleDebug'}else{'assembleRelease'}
    if($RunUiChecks){
        $env:IOR_UI_TEST_CLASSPATH=Join-Path $output 'ui-test-classpath.txt'
        Invoke-AndroidBuildTool $gradle @('-p','android','--no-daemon','exportUiTestClasspath') (Join-Path $output 'unit-compile.log')
        Invoke-AndroidBuildTool 'node.exe' @('scripts/run-android-ui-tests.cjs',$env:IOR_UI_TEST_CLASSPATH) (Join-Path $output 'unit-tests.log')
    }
    Invoke-AndroidBuildTool $gradle @('-p','android','--no-daemon',':app:clean',$task) (Join-Path $output 'apk-build.log')
    $variant=if($Debug){'debug'}else{'release'}
    $apk=Join-Path $output 'ior-android-arm64.apk'
    Copy-Item -LiteralPath "android/app/build/outputs/apk/$variant/app-$variant.apk" -Destination $apk
    & "$PSScriptRoot/Assert-AndroidApkClasses.ps1" -Apk $apk
    Invoke-AndroidBuildTool (Join-Path $env:ANDROID_HOME 'build-tools/36.0.0/apksigner.bat') @('verify',$apk)
    if($ReuseVerifiedNative){
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip=[IO.Compression.ZipFile]::OpenRead($apk)
        try {
            $packagedLibraries=@($zip.Entries | Where-Object FullName -Match '\.so$')
            if($packagedLibraries.Count -ne $nativeEntries.Count){throw 'Packaged Android native library set changed'}
            foreach($expected in $nativeEntries){
                $entry=$zip.GetEntry($expected.path)
                if(!$entry){throw 'Packaged native library missing'}
                $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
                try{$actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
                if($actual -ne $expected.sha256){throw 'Packaged Android native library changed'}
            }
        } finally {$zip.Dispose()}
        $signer=Join-Path $env:ANDROID_HOME 'build-tools/36.0.0/apksigner.bat'
        function Read-UiApkCertificate([string]$File){
            $ErrorActionPreference='Continue'
            $report=& $signer verify --print-certs $File 2>&1
            if($LASTEXITCODE -ne 0){throw 'APK certificate verification failed'}
            $digest=($report | ForEach-Object {if("$_" -match '^Signer #1 certificate SHA-256 digest: (\w+)$'){$Matches[1]}} | Select-Object -First 1)
            if(!$digest){throw 'APK certificate digest missing'}
            return $digest
        }
        if((Read-UiApkCertificate "$baseline/distandroid/ior-android-arm64.apk") -ne (Read-UiApkCertificate $apk)){throw 'Android signing certificate changed'}
    }
    Write-Host "APK ready: $apk"
} finally { Pop-Location; Remove-Item Env:IOR_ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue;Remove-Item Env:IOR_ANDROID_UI_ASSETS -ErrorAction SilentlyContinue;Remove-Item Env:IOR_ANDROID_UI_JNI -ErrorAction SilentlyContinue;Remove-Item Env:IOR_UI_TEST_CLASSPATH -ErrorAction SilentlyContinue }
