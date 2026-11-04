#!/bin/bash
# Intel (x86_64) と Apple Silicon (arm64) を 1 つの .app にまとめる。
# 使い方: ./mac/build.sh
# 起動:   open dist/SysmonWidget.app
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SDK="$(xcrun --show-sdk-path)"
MIN="12.0"
APP="$ROOT/dist/SysmonWidget.app"
OBJ="$ROOT/dist/obj"

rm -rf "$APP"
mkdir -p "$OBJ" "$APP/Contents/MacOS"

compile() {
    local arch="$1"
    echo "Compiling $arch..."
    swiftc \
        -swift-version 5 \
        -O \
        -target "${arch}-apple-macosx${MIN}" \
        -sdk "$SDK" \
        -framework AppKit \
        -framework IOKit \
        -framework CoreWLAN \
        -framework Security \
        -o "$OBJ/SysmonWidget-${arch}" \
        "$ROOT/mac/main.swift" \
        "$ROOT/mac/SysmonWidget.swift" \
        "$ROOT/mac/GmailClient.swift"
}

compile x86_64
compile arm64

lipo -create \
    -output "$APP/Contents/MacOS/SysmonWidget" \
    "$OBJ/SysmonWidget-x86_64" \
    "$OBJ/SysmonWidget-arm64"

cp "$ROOT/mac/Info.plist" "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/SysmonWidget"
codesign --force --sign - "$APP"

echo "Built: $APP"
lipo -info "$APP/Contents/MacOS/SysmonWidget"
