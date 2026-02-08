#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net9.0/osx-arm64/publish"
APP_NAME="FreeSO.app"
INSTALL_DIR="/Applications"

cd "$SCRIPT_DIR"

echo "Building FSO.MacOS..."
dotnet publish -c Release --self-contained true

echo "Removing existing $APP_NAME from $INSTALL_DIR..."
rm -rf "$INSTALL_DIR/$APP_NAME"

echo "Copying $APP_NAME to $INSTALL_DIR..."
cp -R "$PUBLISH_DIR/$APP_NAME" "$INSTALL_DIR/"

echo "Launching $APP_NAME..."
open "$INSTALL_DIR/$APP_NAME"

echo "Done."
