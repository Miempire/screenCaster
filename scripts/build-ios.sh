#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-all}"
export CARGO_TARGET_DIR="$ROOT/native/target"
export IOS_BUNDLE_ID="${IOS_BUNDLE_ID:-org.screencast.app}"
IOS="$ROOT/apps/ios"
BUILD="$IOS/build"
check() {
  cargo fmt --manifest-path "$ROOT/native/Cargo.toml" -- --check
  cargo clippy --manifest-path "$ROOT/native/Cargo.toml" --locked --all-targets -- -D warnings
  cargo test --manifest-path "$ROOT/native/Cargo.toml" --locked
}
framework() {
  for target in aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios; do
    cargo build --manifest-path "$ROOT/native/Cargo.toml" --locked --release --target "$target"
  done
  mkdir -p "$BUILD/simulator" "$IOS/Frameworks"
  lipo -create "$CARGO_TARGET_DIR/aarch64-apple-ios-sim/release/libscreencast_core.a" \
    "$CARGO_TARGET_DIR/x86_64-apple-ios/release/libscreencast_core.a" \
    -output "$BUILD/simulator/libscreencast_core.a"
  # These exact generated paths belong to this build and are safe to replace.
  rm -rf "$IOS/Frameworks/ScreenCastCore.xcframework"
  xcodebuild -create-xcframework \
    -library "$CARGO_TARGET_DIR/aarch64-apple-ios/release/libscreencast_core.a" -headers "$ROOT/native/include" \
    -library "$BUILD/simulator/libscreencast_core.a" -headers "$ROOT/native/include" \
    -output "$IOS/Frameworks/ScreenCastCore.xcframework"
  xcodegen generate --spec "$IOS/project.yml" --project "$IOS"
}
simulator() {
  xcodebuild -project "$IOS/ScreenCast.xcodeproj" -scheme ScreenCast -configuration Debug \
    -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' \
    -derivedDataPath "$BUILD/DerivedData" CODE_SIGNING_ALLOWED=NO build
}
archive() {
  xcodebuild -project "$IOS/ScreenCast.xcodeproj" -scheme ScreenCast -configuration Release \
    -destination 'generic/platform=iOS' -archivePath "$BUILD/ScreenCast-unsigned.xcarchive" \
    CODE_SIGNING_ALLOWED=NO archive
}
case "$MODE" in
  check) check ;;
  framework) framework ;;
  simulator) simulator ;;
  archive) archive ;;
  all) check; framework; simulator; archive ;;
  *) echo "Usage: $0 [all|check|framework|simulator|archive]" >&2; exit 2 ;;
esac
