# Release 1.2.6 verification

- Windows distribution and Android release builds completed; Windows 10 uses the new native core, Windows 11 retains its pinned native baseline.
- Working-tree .NET suite: 460 passed. Public application tree: 454 passed, 3 legacy sing-box integration theories explicitly skipped because that engine is no longer distributed.
- Native Rust suite: 156 passed, 12 optional scenarios ignored. Android JUnit: 58 passed. Desktop and Android DOM/bridge checks passed.
- The updater code loaded from the actual published 1.2.4 and 1.2.5 assemblies selected the correct Windows 10/11 ZIP, retained dismissal within a session, offered again in a new session, rejected download failure, wrong-version archives and the opposite Windows package.
- Both old versions installed 1.2.6 on isolated copies of both Windows packages; every installed package file matched its source hash and an extra user file remained unchanged.
- Real restart via the old embedded installer succeeded for all four old-version/package combinations, including paths containing spaces and non-ASCII characters. The new application created a window. Test application data was isolated and startup network operations were disabled; no VPN was connected by these tests.
- Locked-file rollback and preservation of user files passed. The embedded installer bytes in 1.2.4, 1.2.5 and this release are identical.
- Android package identity and signing certificate match 1.2.5; versionCode increased from 21 to 22. No physical phone was attached, so actual in-place installation on a device was not performed.

The update UI was checked using the DOM/bridge stand; the old downloader and installer were exercised through their actual managed code. This does not represent clicking through every old-version UI on separate physical Windows 10/11 computers. Public release downloads and the latest-release API must additionally be checked after publication.
