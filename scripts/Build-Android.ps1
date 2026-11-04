param([switch]$SkipNative, [switch]$Debug, [string]$OutputDirectory='android-output/current')
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
New-Item -ItemType Directory -Force $output | Out-Null
Push-Location $workspaceRoot
try {
    & node android/build-ui.mjs
    if($LASTEXITCODE -ne 0){throw 'Android interface build failed'}
    & node android/build-geodata.mjs
    if($LASTEXITCODE -ne 0){throw 'Routing geodata build failed'}
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
    $native=Join-Path $workspaceRoot 'ior-core/target/aarch64-linux-android/release/libior_android.so'
    if(!(Test-Path -LiteralPath $native)){throw 'Native ior-core library missing'}
    $jni=Join-Path $workspaceRoot 'android/app/src/main/jniLibs/arm64-v8a'
    New-Item -ItemType Directory -Force $jni | Out-Null
    Copy-Item -LiteralPath $native -Destination (Join-Path $jni 'libior_android.so')
    & (Join-Path $compiler 'llvm-strip.exe') --strip-unneeded (Join-Path $jni 'libior_android.so')
    if($LASTEXITCODE -ne 0){throw 'Native strip failed'}
    if(!$Debug) {
        $keys=Join-Path $workspaceRoot '.local/android-signing'
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
    Invoke-AndroidBuildTool $gradle @('-p','android','--no-daemon',$task) (Join-Path $output 'apk-build.log')
    $variant=if($Debug){'debug'}else{'release'}
    $apk=Join-Path $output 'ior-android-arm64.apk'
    Copy-Item -LiteralPath "android/app/build/outputs/apk/$variant/app-$variant.apk" -Destination $apk
    Invoke-AndroidBuildTool (Join-Path $env:ANDROID_HOME 'build-tools/36.0.0/apksigner.bat') @('verify',$apk)
    ((Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant()+'  ior-android-arm64.apk') | Set-Content -LiteralPath (Join-Path $output 'checksums.sha256') -Encoding utf8
    Write-Host "APK ready: $apk"
} finally { Pop-Location; Remove-Item Env:IOR_ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue }
