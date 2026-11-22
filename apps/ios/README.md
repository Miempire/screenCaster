# ScreenCast iOS foundation

SwiftUI Host/Viewer selector calls the Rust C ABI through the `ScreenCastCore` Clang module in an XCFramework. The interface reports version, selected role and idle status. It does not implement streaming.

Pending: ReplayKit capture/broadcast extension and consent flow, codecs, authenticated network/session integration, rendering and input. Host selection is only local state.

## macOS build

Requirements: Xcode with iOS SDK (CI selects Xcode 16.2), XcodeGen, Rust 1.83.0; iOS deployment minimum 16.0. Default bundle ID: `org.screencast.app`. Set `IOS_BUNDLE_ID` before running the build scripts to change it; CI reads the repository variable `IOS_BUNDLE_ID`. Direct XcodeGen invocations must set this environment variable too. The same value must be used for generation and export.

```sh
brew install xcodegen
rustup target add aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios
bash scripts/build-ios.sh
```

The script runs fmt/clippy/tests, builds ARM64 device plus ARM64/x86_64 simulator static libraries, combines only simulator libraries using lipo, creates `Frameworks/ScreenCastCore.xcframework`, generates `ScreenCast.xcodeproj` from `project.yml`, checks the simulator build and creates `build/ScreenCast-unsigned.xcarchive` with signing disabled. It never combines device and simulator libraries into one fat library. The XCFramework has a stable C header and module map. Generated projects/frameworks/build artifacts are ignored.

Unsigned archives are compilation artifacts, not installable device apps. In Xcode, choose a simulator and run the generated project; verify version `0.1.0` and role switching with idle status. Archive generation alone is not an App Store validation or on-device execution check.

## Optional signed IPA export

`bash scripts/build-ios-export.sh` exports only when **all five** environment variables / CI secrets are nonempty:

- `IOS_CERTIFICATE_P12_BASE64`: base64 Apple Distribution certificate and private key (.p12).
- `IOS_CERTIFICATE_PASSWORD`: password for the .p12.
- `IOS_PROVISION_PROFILE_BASE64`: base64 App Store distribution provisioning profile matching `IOS_BUNDLE_ID` (default `org.screencast.app`).
- `IOS_TEAM_ID`: Apple Developer team ID.
- `IOS_KEYCHAIN_PASSWORD`: temporary signing keychain password.

Use an isolated macOS runner. The script creates a temporary keychain, installs the profile, archives with manual distribution signing, exports with `method=app-store-connect` and `destination=export`, then removes the temporary keychain/profile. IPA output is `build/ipa/*.ipa`. Missing secrets skip export successfully; invalid supplied secrets fail the job. The profile must match bundle ID/team/certificate. Apple submission metadata and final App Store validation remain outside this build foundation.

CI always saves unsigned archive + XCFramework; when signing succeeds it saves the TestFlight-compatible IPA separately. It never uploads to App Store Connect and never creates a GitHub release. Release/tag publishing is owned by the coordinating workflow.
