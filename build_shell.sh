#!/usr/bin/env bash
# Swift 壳打包（开发态 bundle）：swift build + 组装 X-iTrader-Client.app（输出到仓库上一级）
#
# 开发态 bundle 直接复用仓库的 .venv 与 src/（Resources/backend_root.txt 指向仓库根），
# 因此壳进程 spawn 的后端与 `python -m src.backend` 完全一致，data/ 共用仓库根。
# 生产打包（backend PyInstaller 化 + runtime 内嵌）见 build_app.sh。
#
# 用法: ./build_shell.sh [debug|release]   默认 release
set -euo pipefail
cd "$(dirname "$0")"

CONFIG="${1:-release}"

echo "==> swift build -c $CONFIG"
(cd shell && swift build -c "$CONFIG")
BIN_PATH="$(cd shell && swift build -c "$CONFIG" --show-bin-path)/iTrader"
[ -x "$BIN_PATH" ] || { echo "错误: 未找到编译产物 $BIN_PATH"; exit 1; }

VERSION="$(sed -n 's/^__version__ = "\(.*\)"/\1/p' src/__init__.py)"
# 产物落到仓库上一级（X-iTrader/），与 build_app.sh 一致，便于直接分发
OUT_DIR="$(cd .. && pwd)"
APP="$OUT_DIR/X-iTrader-Client.app"
CONTENTS="$APP/Contents"

echo "==> 组装 $APP (v$VERSION)"
rm -rf "$APP"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources"

cp "$BIN_PATH" "$CONTENTS/MacOS/X-iTrader-Client"
printf '%s' "$PWD" > "$CONTENTS/Resources/backend_root.txt"

cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>X-iTrader-Client</string>
    <key>CFBundleDisplayName</key><string>X-iTrader-Client</string>
    <key>CFBundleIdentifier</key><string>com.itrader.shell</string>
    <!-- SwiftUI 默认菜单栏（文件/编辑/显示/窗口/帮助）由系统按本地化生成，
         声明开发区域为中文后即使系统首选语言是英文也显示中文 -->
    <key>CFBundleDevelopmentRegion</key><string>zh-Hans</string>
    <key>CFBundleExecutable</key><string>X-iTrader-Client</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST

echo "==> ad-hoc 签名"
codesign --force --sign - "$APP"

echo "完成: $APP"
