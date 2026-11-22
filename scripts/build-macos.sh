#!/usr/bin/env bash
#
# macOS client build.
#
#   scripts/build-macos.sh [all|check|targets|framework|app|app-x64|app-arm64|run|verify]
#
#   check      cargo fmt / clippy / test on the Rust core (unchanged from build-ios.sh)
#   targets    cargo build the core for aarch64-apple-darwin and x86_64-apple-darwin
#   framework  lipo the two static libs into one universal archive and package it as an xcframework
#   app        xcodegen the project and build a universal ScreenCast.app (arm64 + x86_64)
#   app-x64    build the app for Intel MacBook only (x86_64)
#   app-arm64  build the app for Apple silicon only (arm64)
#   run        open the built app
#   verify     print the architectures in the universal archive, app bundle and xcframework layout
#
# The script refuses to run off macOS instead of pretending: it cannot cross-compile the Swift app
# from Windows/Linux.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-all}"

MAC="$ROOT/apps/macos"
BUILD="$MAC/build"
LIB="libscreencast_core.a"
CRATE="$ROOT/native/Cargo.toml"
export CARGO_TARGET_DIR="$ROOT/native/target"
export MACOS_BUNDLE_ID="${MACOS_BUNDLE_ID:-org.screencast.macos}"

TARGETS=(aarch64-apple-darwin x86_64-apple-darwin)

require_macos() {
  if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "build-macos.sh: 需要 macOS（当前 $(uname -s)），Swift/xcodebuild 无法交叉编译。" >&2
    exit 2
  fi
}

require_tool() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "build-macos.sh: 缺少 $1，请先安装。" >&2
    exit 2
  fi
}

check() {
  cargo fmt --manifest-path "$CRATE" -- --check
  cargo clippy --manifest-path "$CRATE" --locked --all-targets -- -D warnings
  cargo test --manifest-path "$CRATE" --locked
}

targets() {
  require_macos
  require_tool cargo
  require_tool rustup
  for target in "${TARGETS[@]}"; do
    if ! rustup target list --installed | grep -qx "$target"; then
      rustup target add "$target"
    fi
    cargo build --manifest-path "$CRATE" --locked --release --target "$target"
  done
}

framework() {
  require_macos
  require_tool lipo
  require_tool xcodebuild
  targets

  mkdir -p "$BUILD/universal" "$MAC/Frameworks"
  lipo -create \
    "$CARGO_TARGET_DIR/aarch64-apple-darwin/release/$LIB" \
    "$CARGO_TARGET_DIR/x86_64-apple-darwin/release/$LIB" \
    -output "$BUILD/universal/$LIB"

  # These generated paths belong to this build and are safe to replace.
  rm -rf "$MAC/Frameworks/ScreenCastCore.xcframework"
  xcodebuild -create-xcframework \
    -library "$BUILD/universal/$LIB" -headers "$ROOT/native/include" \
    -output "$MAC/Frameworks/ScreenCastCore.xcframework"

  require_tool xcodegen
  xcodegen generate --spec "$MAC/project.yml" --project "$MAC"
}

app() {
  build_app "arm64 x86_64"
}

app_x64() {
  build_app "x86_64"
}

app_arm64() {
  build_app "arm64"
}

build_app() {
  require_macos
  require_tool xcodebuild
  local archs="$1"
  if [[ ! -d "$MAC/ScreenCastMac.xcodeproj" ]]; then
    framework
  fi
  # Ad-hoc signing: TCC (screen recording, accessibility) keys off the code signature, and an
  # unsigned bundle is not accepted as a client. `-` produces an ad-hoc identity with no team.
  xcodebuild -project "$MAC/ScreenCastMac.xcodeproj" -scheme ScreenCastMac -configuration Debug \
    -derivedDataPath "$BUILD/DerivedData" \
    ARCHS="$archs" ONLY_ACTIVE_ARCH=NO \
    CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="-" DEVELOPMENT_TEAM="" \
    CODE_SIGNING_REQUIRED=YES CODE_SIGNING_ALLOWED=YES \
    build
}

run_app() {
  require_macos
  local bundle="$BUILD/DerivedData/Build/Products/Debug/ScreenCast.app"
  if [[ ! -d "$bundle" ]]; then
    echo "build-macos.sh: 找不到 $bundle，先运行 app。" >&2
    exit 2
  fi
  open "$bundle"
}

verify() {
  require_macos
  if [[ ! -f "$BUILD/universal/$LIB" ]]; then
    echo "build-macos.sh: 还没有 $BUILD/universal/$LIB，先运行 framework。" >&2
    exit 2
  fi
  echo "== lipo -info =="
  lipo -info "$BUILD/universal/$LIB"
  echo "== 导出符号（应为 14 个 sc_ 符号）=="
  nm -gU "$BUILD/universal/$LIB" | grep -c ' _sc_' || true
  nm -gU "$BUILD/universal/$LIB" | grep ' _sc_' | sed 's/.* _/  /'
  echo "== xcframework 布局 =="
  find "$MAC/Frameworks/ScreenCastCore.xcframework" -maxdepth 3 -print | sed "s|$MAC/||"
  local binary="$BUILD/DerivedData/Build/Products/Debug/ScreenCast.app/Contents/MacOS/ScreenCast"
  if [[ -f "$binary" ]]; then
    echo "== app 架构 =="
    lipo -info "$binary"
  fi
}

case "$MODE" in
  check) check ;;
  targets) targets ;;
  framework) framework ;;
  app) app ;;
  app-x64) app_x64 ;;
  app-arm64) app_arm64 ;;
  run) run_app ;;
  verify) verify ;;
  all) check; framework; app ;;
  *) echo "用法: $0 [all|check|targets|framework|app|app-x64|app-arm64|run|verify]" >&2; exit 2 ;;
esac
