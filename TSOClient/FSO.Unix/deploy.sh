#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

OS="$(uname -s)"

if [ "$OS" = "Darwin" ]; then
    RID="osx-arm64"
elif [ "$OS" = "Linux" ]; then
    RID="linux-x64"
else
    echo "Unsupported platform: $OS" && exit 1
fi

PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net9.0/$RID/publish"

echo "Building FSO.Unix for $OS..."
dotnet publish -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true

if [ "$OS" = "Darwin" ]; then
    rm -rf /Applications/OpenSO.app
    cp -R "$PUBLISH_DIR/OpenSO.app" /Applications/
    open /Applications/OpenSO.app
else
    INSTALL_DIR="$HOME/.local/share/OpenSO"
    mkdir -p "$INSTALL_DIR"
    rm -rf "$INSTALL_DIR"/*
    cp -R "$PUBLISH_DIR"/* "$INSTALL_DIR/"
    chmod +x "$INSTALL_DIR/OpenSO"

    mkdir -p "$HOME/.local/share/icons"
    cp "$SCRIPT_DIR/fso.png" "$HOME/.local/share/icons/freeso.png"

    cat > "$HOME/.local/share/applications/OpenSO.desktop" << EOF
[Desktop Entry]
Name=OpenSO
Comment=Free re-implementation of The Sims Online
Exec=$INSTALL_DIR/OpenSO
Icon=$HOME/.local/share/icons/freeso.png
Terminal=false
Type=Application
Categories=Game;
EOF

    "$INSTALL_DIR/OpenSO" &
fi

echo "Done."
