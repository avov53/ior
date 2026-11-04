# Android components

The ior-core source and bundled `assets/licenses` retain the original notices
for the Rust core, AWG/BoringTun, Hysteria2, Reality and web interface dependencies.

Android-specific dependencies:

- AndroidX Core 1.16.0, Activity 1.10.1, WebKit 1.14.0, CameraX 1.4.2,
  WorkManager 2.10.1: Android Open Source Project, Apache-2.0.
- CameraX native image processing includes libyuv, BSD-3-Clause.
- ZXing Core 3.5.3: ZXing authors, Apache-2.0.
- OkHttp 4.12.0 and Okio 3.6.0: Square and contributors, Apache-2.0.
- SnakeYAML 2.4: SnakeYAML contributors, Apache-2.0.
- Kotlin runtime 2.2.21: JetBrains and contributors, Apache-2.0.
- JNI Rust 0.21.1 and JNI system bindings: MIT OR Apache-2.0.

Dependency versions are pinned in the Gradle files and Cargo.lock. Test-only
JUnit, MockWebServer and okhttp-tls are not shipped in the release APK.
Java, Gradle, Android SDK and NDK are build tools; they are not included in APK.
