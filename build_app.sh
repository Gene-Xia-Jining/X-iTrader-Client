#!/usr/bin/env bash
# 生产打包：Swift 壳（release）+ PyInstaller headless 后端，组装独立可分发
# 的 X-iTrader-Client.app。
# 与 build_shell.sh（开发态 bundle，复用仓库 .venv）不同，本脚本产物自包含：
#
#   Contents/MacOS/X-iTrader-Client   Swift 壳（CFBundleExecutable）
#   Contents/Resources/backend/       PyInstaller onedir 后端（backend 可执行 + 依赖）
#
# 后端放 Resources 而非 MacOS：codesign 把 MacOS/ 下的数据目录（*.dist-info、
# 非 Mach-O 可执行位文件）误判为未签名嵌套代码导致无法签名；Resources 只做
# 资源封印，可整体通过 ad-hoc 签名。
# 后端运行时的目标位置是数据根下的 ~/.X-iTrader-Client/runtime/backend/
# （见 src/infrastructure/paths.py 与 shell AppPaths.swift）；frozen 后端的 workdir
# 即数据根 ~/.X-iTrader-Client（src/backend/__main__.py frozen 分支 chdir），data/
# 独立于 app 位置。本脚本当前仍把 backend 打进 bundle 作为过渡，壳的 detectPlan
# 优先探测 runtime 布局、回退到 Bundle（见 BackendProcess.swift）。
#
# 用法: ./build_app.sh
set -euo pipefail
cd "$(dirname "$0")"

echo "==> swift build -c release"
(cd shell && swift build -c release)
BIN_PATH="$(cd shell && swift build -c release --show-bin-path)/iTrader"
[ -x "$BIN_PATH" ] || { echo "错误: 未找到编译产物 $BIN_PATH"; exit 1; }

echo "==> pyinstaller backend (onedir)"
./.venv/bin/pyinstaller --workpath=build/backend --distpath=dist --noconfirm specs/backend.spec
[ -x dist/backend/backend ] || { echo "错误: 未找到后端产物 dist/backend/backend"; exit 1; }

VERSION="$(sed -n 's/^__version__ = "\(.*\)"/\1/p' src/__init__.py)"
# 最终 .app 落到仓库上一级（X-iTrader/，与 X-iTrader-Server 等并列）便于分发；
# PyInstaller 的 dist/ 中间产物仍留在仓库内（已被 .gitignore 忽略）。
OUT_DIR="$(cd .. && pwd)"
APP="$OUT_DIR/X-iTrader-Client.app"
CONTENTS="$APP/Contents"

echo "==> 组装 $APP ($VERSION)"
rm -rf "$APP"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources"
cp "$BIN_PATH" "$CONTENTS/MacOS/X-iTrader-Client"
cp -R dist/backend "$CONTENTS/Resources/backend"
# AGPL-3.0 第 4 节：分发目标码时须附带许可证副本
cp LICENSE "$CONTENTS/Resources/LICENSE"

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
# 逐个签 Mach-O 后再签外层 bundle：backend onedir 依赖里混有 dist-info 等非代码
# 目录，--deep 会把它们当子 bundle 处理而报 "bundle format unrecognized"
find "$CONTENTS" -type f -print0 | while IFS= read -r -d '' f; do
    if file "$f" | grep -q "Mach-O"; then
        codesign --force --sign - "$f" 2>/dev/null || true
    fi
done
codesign --force --sign - "$APP"

echo "完成: $APP"
