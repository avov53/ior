# Native core distribution from 1.2.6

The public application uses the versioned native binaries in `core/ior-core` and
`core/android`. `core/native-package-manifest.json` pins their SHA-256 hashes.
Windows 10 and Windows 11 intentionally use different native builds.

New ior-core implementation changes are not included in this public repository.
Previously published native sources remain in Git history and in the tree for
historical reference; they are not the source of the current release binaries.
The distribution scripts use the pinned binaries instead of rebuilding those
historical sources. Native implementation development requires the private source
checkout. Existing third-party license notices continue to accompany the binaries.

Android release signing requires the existing private signing key; it is never
included here. A developer debug APK uses their own debug signature and cannot
replace the official release APK without uninstalling it.
