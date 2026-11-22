#!/usr/bin/env bash
# Export only; deliberately never uploads to App Store Connect.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IOS="$ROOT/apps/ios"
export IOS_BUNDLE_ID="${IOS_BUNDLE_ID:-org.screencast.app}"
for name in IOS_CERTIFICATE_P12_BASE64 IOS_CERTIFICATE_PASSWORD IOS_PROVISION_PROFILE_BASE64 IOS_TEAM_ID IOS_KEYCHAIN_PASSWORD; do
  if [[ -z "${!name:-}" ]]; then
    echo "Signing secrets incomplete: skipping IPA export."
    exit 0
  fi
done
SIGNING="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/screencast-signing.XXXXXX")"
KEYCHAIN="$SIGNING/signing.keychain-db"
PROFILE_PATH=""
cleanup() {
  if [[ -f "$SIGNING/keychains.json" ]]; then
    python3 - "$SIGNING/keychains.json" <<'PY'
import json, subprocess, sys
subprocess.run(['security', 'list-keychains', '-d', 'user', '-s', *json.load(open(sys.argv[1]))], check=False)
PY
  fi
  security delete-keychain "$KEYCHAIN" >/dev/null 2>&1 || true
  if [[ -n "$PROFILE_PATH" ]]; then rm -f "$PROFILE_PATH"; fi
  rm -rf "$SIGNING"
}
trap cleanup EXIT
export SIGNING
python3 - <<'PY'
import base64, os
from pathlib import Path
p = Path(os.environ['SIGNING'])
for env, name in [('IOS_CERTIFICATE_P12_BASE64', 'certificate.p12'), ('IOS_PROVISION_PROFILE_BASE64', 'profile.mobileprovision')]:
    (p / name).write_bytes(base64.b64decode(os.environ[env], validate=True))
PY
security create-keychain -p "$IOS_KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 21600 "$KEYCHAIN"
security unlock-keychain -p "$IOS_KEYCHAIN_PASSWORD" "$KEYCHAIN"
python3 - "$SIGNING/keychains.json" "$KEYCHAIN" <<'PY'
import json, shlex, subprocess, sys
existing = shlex.split(subprocess.check_output(['security', 'list-keychains', '-d', 'user'], text=True))
with open(sys.argv[1], 'w') as f:
    json.dump(existing, f)
subprocess.run(['security', 'list-keychains', '-d', 'user', '-s', *existing, sys.argv[2]], check=True)
PY
security import "$SIGNING/certificate.p12" -P "$IOS_CERTIFICATE_PASSWORD" -A -t cert -f pkcs12 -k "$KEYCHAIN"
security set-key-partition-list -S apple-tool:,apple: -k "$IOS_KEYCHAIN_PASSWORD" "$KEYCHAIN" >/dev/null
security cms -D -i "$SIGNING/profile.mobileprovision" > "$SIGNING/profile.plist"
python3 - <<'PY'
import os, plistlib
from pathlib import Path
with (Path(os.environ['SIGNING']) / 'profile.plist').open('rb') as f:
    profile = plistlib.load(f)
team = os.environ['IOS_TEAM_ID']
bundle = os.environ['IOS_BUNDLE_ID']
entitlements = profile.get('Entitlements', {})
if team not in profile.get('TeamIdentifier', []):
    raise SystemExit('Profile team does not match IOS_TEAM_ID')
identifier = entitlements.get('application-identifier', '')
prefixes = profile.get('ApplicationIdentifierPrefix', [])
if identifier not in [prefix + '.' + bundle for prefix in prefixes]:
    raise SystemExit('Profile application-identifier does not match IOS_BUNDLE_ID')
if profile.get('ProvisionedDevices') or profile.get('ProvisionsAllDevices') or entitlements.get('get-task-allow'):
    raise SystemExit('An App Store distribution profile is required')
PY
PROFILE_UUID="$(/usr/libexec/PlistBuddy -c 'Print UUID' "$SIGNING/profile.plist")"
[[ "$PROFILE_UUID" =~ ^[A-Fa-f0-9-]+$ ]] || { echo 'Invalid profile UUID'; exit 1; }
PROFILE_PATH="$HOME/Library/MobileDevice/Provisioning Profiles/$PROFILE_UUID.mobileprovision"
mkdir -p "$(dirname "$PROFILE_PATH")"
# Avoid replacing a pre-existing local profile during a manual invocation.
if [[ -e "$PROFILE_PATH" ]]; then echo 'Provisioning profile already exists; use an isolated CI runner'; PROFILE_PATH=''; exit 1; fi
cp "$SIGNING/profile.mobileprovision" "$PROFILE_PATH"
export PROFILE_UUID
python3 - <<'PY'
import os, plistlib
from pathlib import Path
options = {
    'method': 'app-store-connect', 'destination': 'export',
    'teamID': os.environ['IOS_TEAM_ID'], 'signingStyle': 'manual',
    'signingCertificate': 'Apple Distribution',
    'provisioningProfiles': {os.environ['IOS_BUNDLE_ID']: os.environ['PROFILE_UUID']},
    'manageAppVersionAndBuildNumber': False,
}
with (Path(os.environ['SIGNING']) / 'ExportOptions.plist').open('wb') as f:
    plistlib.dump(options, f)
PY
xcodebuild -project "$IOS/ScreenCast.xcodeproj" -scheme ScreenCast -configuration Release \
  -destination 'generic/platform=iOS' -archivePath "$IOS/build/ScreenCast-signed.xcarchive" \
  DEVELOPMENT_TEAM="$IOS_TEAM_ID" CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY='Apple Distribution' \
  PROVISIONING_PROFILE_SPECIFIER="$PROFILE_UUID" OTHER_CODE_SIGN_FLAGS="--keychain $KEYCHAIN" archive
xcodebuild -exportArchive -archivePath "$IOS/build/ScreenCast-signed.xcarchive" \
  -exportOptionsPlist "$SIGNING/ExportOptions.plist" -exportPath "$IOS/build/ipa"
