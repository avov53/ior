$ErrorActionPreference='Stop'
$workspaceRoot=Split-Path $PSScriptRoot -Parent
$tools=Join-Path $workspaceRoot '.local/android-tools'
$env:JAVA_HOME=(Get-ChildItem (Join-Path $tools 'java') -Directory|Select-Object -First 1).FullName
$env:ANDROID_HOME=Join-Path $tools 'sdk'
$env:ANDROID_USER_HOME=Join-Path $tools 'android-user'
New-Item -ItemType Directory -Force $env:ANDROID_USER_HOME | Out-Null
$env:GRADLE_USER_HOME=& cmd.exe /d /c "for %I in (`"$tools\gradle-home`") do @echo %~sI"
$shortTemp=& cmd.exe /d /c "for %I in (`"$tools\tmp`") do @echo %~sI"
$env:JAVA_TOOL_OPTIONS="-Dfile.encoding=UTF-8 -Djdk.net.unixdomain.tmpdir=$shortTemp -Djava.io.tmpdir=$shortTemp"
Push-Location $workspaceRoot
try {
    & node android/build-ui.mjs
    if($LASTEXITCODE -ne 0){throw 'UI build failed'}
    & node android/verify-ui.mjs
    if($LASTEXITCODE -ne 0){throw 'Android UI checks failed'}
    $ErrorActionPreference='Continue'
    & (Join-Path $tools 'gradle/gradle-8.13/bin/gradle.bat') -p android --no-daemon compileDebugUnitTestKotlin *> android-output/current/unit-compile.log
    $compileExit=$LASTEXITCODE
    $ErrorActionPreference='Stop'
    if($compileExit -ne 0){Get-Content android-output/current/unit-compile.log -Tail 25;throw 'Unit test compilation failed'}
    # Gradle 8.13 test worker @argfile fails on some Windows Cyrillic paths.
    # Run the compiled JUnit classes directly with UTF-16 process arguments.
    $classpath=@((Resolve-Path 'android/app/build/tmp/kotlin-classes/debugUnitTest').Path,(Resolve-Path 'android/app/build/tmp/kotlin-classes/debug').Path)
    $classpath+=Get-ChildItem (Join-Path $tools 'gradle-home/caches/modules-2/files-2.1') -Filter '*.jar' -Recurse | Where-Object {$_.Name -match '^(junit-4|hamcrest-core|kotlin-stdlib-2\.2\.21|json-2024|snakeyaml-2|okhttp-4|okhttp-tls-4|mockwebserver-4|okio-jvm|bcprov-|bcpkix-|bcutil-|core-3\.5\.3)'} | ForEach-Object {$_.FullName}
    $classpath+=Join-Path $tools 'sdk/platforms/android-36/android.jar'
    $ErrorActionPreference='Continue'
    & (Join-Path $env:JAVA_HOME 'bin/java.exe') -cp ($classpath -join ';') org.junit.runner.JUnitCore app.ior.android.CodecTests app.ior.android.SubscriptionTests app.ior.android.QrTests app.ior.android.SubscriptionOptionsTests app.ior.android.DiagnosticTests *> android-output/current/unit-tests.log
    $testExit=$LASTEXITCODE
    $ErrorActionPreference='Stop'
    Get-Content android-output/current/unit-tests.log -Tail 25
    if($testExit -ne 0){throw 'Android unit tests failed'}
}finally{Pop-Location}
