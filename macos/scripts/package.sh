#!/bin/bash
set -euo pipefail
export MACOSX_DEPLOYMENT_TARGET=13.0
cd "$(dirname "$0")/../.."
arch="${1:?architecture required}"
case "$arch" in arm64) target=aarch64-apple-darwin;; x64) target=x86_64-apple-darwin;; *) exit 2;; esac
: "${GITHUB_SHA:?commit required}"
cargo build --manifest-path ior-core/Cargo.toml -p ior-core --release --locked --target "$target"
mkdir -p macos/src-tauri/resources
cp "ior-core/target/$target/release/ior-core" macos/src-tauri/resources/ior-core
node macos/scripts/build-ui.mjs
cli="$(pwd)/.local/tauri-cli/node_modules/.bin/tauri"
(cd macos/src-tauri && "$cli" build --target "$target" --bundles app -- --locked)
app="macos/src-tauri/target/$target/release/bundle/macos/ior.app"
lipo -verify_arch "$(case "$arch" in arm64) echo arm64;; x64) echo x86_64;; esac)" "$app/Contents/MacOS/ior-macos"
codesign --force --sign - "$app/Contents/Resources/resources/ior-core"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
mkdir -p "macos-output/$arch"
cp macos/README.md "macos-output/$arch/README.md"
ditto "$app" "macos-output/$arch/ior.app"
printf '{"commit":"%s","architecture":"%s","version":"1.2.9","vpnAvailable":false}\n' "$GITHUB_SHA" "$arch" > "macos-output/$arch/build.json"
(cd "macos-output/$arch" && ditto -c -k --sequesterRsrc --keepParent ior.app "ior-macos-$arch.zip" && shasum -a 256 "ior-macos-$arch.zip" > SHA256SUMS.txt)
