#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-all}"
export CARGO_TARGET_DIR="$ROOT/native/target"
export ANDROID_NDK_HOME="${ANDROID_NDK_HOME:-${ANDROID_SDK_ROOT:-${ANDROID_HOME:-}}/ndk/27.2.12479018}"
check() {
  cargo fmt --manifest-path "$ROOT/native/Cargo.toml" -- --check
  cargo clippy --manifest-path "$ROOT/native/Cargo.toml" --locked --all-targets -- -D warnings
  cargo test --manifest-path "$ROOT/native/Cargo.toml" --locked
}
native_build() {
  test -d "$ANDROID_NDK_HOME" || { echo "NDK 27.2.12479018 is required" >&2; exit 1; }
  (cd "$ROOT/native" && cargo ndk -t arm64-v8a -t x86_64 \
    -o "$ROOT/apps/android/app/src/main/jniLibs" build --release --locked)
}
gradle_run() {
  gradle --no-daemon -p "$ROOT/apps/android" "$@"
}
case "$MODE" in
  check) check ;;
  native) native_build ;;
  lint) gradle_run lintDebug ;;
  apk) gradle_run assembleDebug ;;
  all) check; gradle_run lintDebug; native_build; gradle_run assembleDebug ;;
  *) echo "Usage: $0 [all|check|native|lint|apk]" >&2; exit 2 ;;
esac
