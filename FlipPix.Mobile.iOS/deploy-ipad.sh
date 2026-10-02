#!/usr/bin/env bash
# Builds FlipPix Mobile, signs it and puts it on an iPad plugged into this Mac (or on the same Wi-Fi
# once paired in Xcode's Devices window), then launches it.
#
#   FlipPix.Mobile.iOS/deploy-ipad.sh                # first connected iPad/iPhone
#   DEVICE=<udid or name> FlipPix.Mobile.iOS/deploy-ipad.sh
#
# Needs: Xcode signed in to an Apple ID (Settings > Accounts), the iPad trusted and in Developer Mode
# (Settings > Privacy & Security > Developer Mode), and xcodegen (brew install xcodegen).
#
# .NET cannot ask Apple for a provisioning profile itself, so each run builds an empty Xcode app
# with the same bundle id and lets xcodebuild create the certificate, register the iPad and fetch the
# profile. A free Personal Team's profile lasts 7 days; run this again to renew it.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
TEAM="${TEAM:-3ZGH372MTL}"
# A free team cannot use a bundle id someone else has registered, so this is not com.flippix.mobile.
BUNDLE_ID="${BUNDLE_ID:-com.zaheerahmed.flippix}"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
STUB="$HERE/obj/provision-stub"

# ---- the device --------------------------------------------------------------------------------
devices_json="$(mktemp)"
xcrun devicectl list devices --json-output "$devices_json" >/dev/null
if [[ -z "${DEVICE:-}" ]]; then
  DEVICE="$(python3 - "$devices_json" <<'PY'
import json, sys
for d in json.load(open(sys.argv[1]))["result"]["devices"]:
    hw = d.get("hardwareProperties", {})
    if hw.get("platform") == "iOS" and hw.get("reality") == "physical" \
       and d.get("connectionProperties", {}).get("pairingState") == "paired":
        print(hw["udid"]); break
PY
)"
fi
rm -f "$devices_json"
if [[ -z "$DEVICE" ]]; then
  echo "No paired iPad found. Plug it in, unlock it, tap Trust, and check Xcode > Window > Devices." >&2
  exit 1
fi
echo "==> Device $DEVICE"

# ---- a provisioning profile for the bundle id --------------------------------------------------
mkdir -p "$STUB/Stub"
cat > "$STUB/Stub/main.swift" <<'SWIFT'
import UIKit
UIApplicationMain(CommandLine.argc, CommandLine.unsafeArgv, nil, nil)
SWIFT
cat > "$STUB/project.yml" <<YAML
name: FlipPixProvision
options: { deploymentTarget: { iOS: "15.0" } }
targets:
  FlipPixProvision:
    type: application
    platform: iOS
    sources: [Stub]
    info: { path: Stub/Info.plist, properties: { UILaunchScreen: {} } }
    settings:
      PRODUCT_BUNDLE_IDENTIFIER: $BUNDLE_ID
      DEVELOPMENT_TEAM: $TEAM
      CODE_SIGN_STYLE: Automatic
      TARGETED_DEVICE_FAMILY: "1,2"
YAML
echo "==> Provisioning $BUNDLE_ID for team $TEAM"
(cd "$STUB" && xcodegen generate --quiet)
xcodebuild -project "$STUB/FlipPixProvision.xcodeproj" -scheme FlipPixProvision \
  -destination "id=$DEVICE" -derivedDataPath "$STUB/dd" \
  -allowProvisioningUpdates -allowProvisioningDeviceRegistration -quiet build

# ---- build, install, launch --------------------------------------------------------------------
APP="$HERE/bin/Release/net9.0-ios/ios-arm64/FlipPix.Mobile.iOS.app"
# An incremental build after a code change can ship AOT code that no longer matches the trimmed
# assemblies, and the app aborts at launch ("Failed to load AOT module ... out of date") without a
# word on the iPad. So when any source changed since the last deploy, the trimmed and AOT outputs go
# and are made again (most of a clean build's time); with no change the build stays incremental.
SOURCES_HASH_FILE="$HERE/obj/deployed-sources.sha"
sources_hash="$(cd "$HERE/.." && find FlipPix.Mobile FlipPix.Mobile.iOS FlipPix.Remote.Contracts \
  \( -name bin -o -name obj \) -prune -o -type f ! -name .DS_Store -print0 \
  | sort -z | xargs -0 shasum | shasum | cut -d' ' -f1)"
if [[ "$(cat "$SOURCES_HASH_FILE" 2>/dev/null)" != "$sources_hash" ]]; then
  echo "==> Sources changed since the last deploy: clearing the AOT outputs"
  AOT_OBJ="$HERE/obj/Release/net9.0-ios/ios-arm64"
  rm -rf "$AOT_OBJ"/{linked,linker-cache,linker-items,stripped,nativelibraries,stamp,aot-instances.dll} "$APP"
fi

# The pinned iOS workload (18.5) asks for Xcode 16.4 by name; it builds fine with newer Xcode.
echo "==> Building FlipPix"
"$DOTNET" build "$HERE" -c Release -r ios-arm64 \
  -p:ValidateXcodeVersion=false \
  -p:ApplicationId="$BUNDLE_ID" \
  -p:CodesignKey="Apple Development" \
  -p:CodesignTeamId="$TEAM"
echo "$sources_hash" > "$SOURCES_HASH_FILE"

echo "==> Installing"
xcrun devicectl device install app --device "$DEVICE" "$APP"
echo "==> Launching"
xcrun devicectl device process launch --device "$DEVICE" "$BUNDLE_ID" || \
  echo "Installed. If it would not launch: on the iPad, Settings > General > VPN & Device Management, trust your Apple ID, then tap FlipPix."
