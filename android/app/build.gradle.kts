plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }
android {
    namespace = "app.ior.android"
    compileSdk = 36
    buildFeatures { buildConfig = true }
    val uiAssets = System.getenv("IOR_ANDROID_UI_ASSETS")
    if (uiAssets != null) sourceSets.getByName("main").assets.setSrcDirs(listOf(uiAssets))
    val uiJni = System.getenv("IOR_ANDROID_UI_JNI")
    if (uiJni != null) sourceSets.getByName("main").jniLibs.setSrcDirs(listOf(uiJni))
    defaultConfig {
        applicationId = "app.ior.android"
        minSdk = 29
        targetSdk = 36
        versionCode = 23
        versionName = "1.2.7"
        ndk { abiFilters += "arm64-v8a" }
    }
    signingConfigs {
        create("localRelease") {
            val keyFile = System.getenv("IOR_ANDROID_KEYSTORE")
            if (keyFile != null) {
                storeFile = file(keyFile)
                storePassword = System.getenv("IOR_ANDROID_KEY_PASSWORD")
                keyAlias = "ior"
                keyPassword = storePassword
            }
        }
    }
    buildTypes {
        getByName("release") { isMinifyEnabled = false; signingConfig = signingConfigs.getByName("localRelease") }
        getByName("debug") { applicationIdSuffix = ".debug" }
    }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    packaging { jniLibs { useLegacyPackaging = false } }
}
dependencies {
    implementation("androidx.core:core-ktx:1.16.0")
    implementation("androidx.activity:activity-ktx:1.10.1")
    implementation("androidx.webkit:webkit:1.14.0")
    implementation("androidx.work:work-runtime-ktx:2.10.1")
    implementation("androidx.camera:camera-core:1.4.2")
    implementation("androidx.camera:camera-camera2:1.4.2")
    implementation("androidx.camera:camera-lifecycle:1.4.2")
    implementation("androidx.camera:camera-view:1.4.2")
    implementation("com.google.zxing:core:3.5.3")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.yaml:snakeyaml:2.4")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
    testImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
    testImplementation("com.squareup.okhttp3:okhttp-tls:4.12.0")
}

// Export the same local test classpath without Gradle's Windows @argfile
// worker launcher, which misreads non-ASCII paths with JDK 17.
tasks.register("exportUiTestClasspath") {
    val unitTest = tasks.named<org.gradle.api.tasks.testing.Test>("testReleaseUnitTest")
    dependsOn("compileReleaseUnitTestKotlin", "compileReleaseUnitTestJavaWithJavac",
        "processReleaseUnitTestJavaRes", "bundleReleaseClassesToRuntimeJar", "processReleaseResources")
    doLast {
        val target = System.getenv("IOR_UI_TEST_CLASSPATH") ?: error("IOR_UI_TEST_CLASSPATH is required")
        file(target).writeText(unitTest.get().classpath.asPath, Charsets.UTF_8)
    }
}
