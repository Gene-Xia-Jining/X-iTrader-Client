#!/usr/bin/env bash
# macOS 生产打包：Swift 壳（release）+ 嵌入式 Python 后端，组装独立可分发的
# X-iTrader-Client.app。
#
#   Contents/MacOS/X-iTrader-Client   Swift 壳（CFBundleExecutable）
#   Contents/Resources/backend/       后端（runtime/ 嵌入式 Python + code/ 源码）
#
# 后端放 Resources 而非 MacOS：codesign 把 MacOS/ 下的数据目录（*.dist-info、
# 非 Mach-O 可执行位文件）误判为未签名嵌套代码导致无法签名；Resources 只做
# 资源封印，可整体通过 ad-hoc 签名。
#
# 后端由 specs/build_backend.py 拼装（python-build-standalone + uv），不再用
# PyInstaller：src 需要以普通目录外置以支持差量更新，打包器的静态分析猜依赖
# 历史上漏收过标准库模块。启动时后端 chdir 到数据根 ~/.X-iTrader-Client
# （ITRADER_PACKAGED 分支），data/ 独立于 app 位置。
#
# 用法: ./build.sh
set -euo pipefail
cd "$(dirname "$0")"

# 清理旧位置残留：dist/ 曾是 PyInstaller 时代的产物中间目录，已并入 build/，
# 整目录移除避免遗留旧产物被误用（与服务端 build.sh 同一处理）
rm -rf dist

echo "==> swift build -c release"
(cd shell && swift build -c release)
BIN_PATH="$(cd shell && swift build -c release --show-bin-path)/iTrader"
[ -x "$BIN_PATH" ] || { echo "错误: 未找到编译产物 $BIN_PATH"; exit 1; }

echo "==> 拼装后端（嵌入式运行时 + uv 装依赖）"
# 拼装脚本只用标准库：本地优先用 .venv 的解释器，CI（没有 .venv）退回系统 python3
if [ -x ./.venv/bin/python ]; then
    PY_BUILD=./.venv/bin/python
else
    PY_BUILD=python3
fi
"$PY_BUILD" specs/build_backend.py --output build/backend
[ -d build/backend/runtime ] && [ -d build/backend/code ] || {
    echo "错误: 后端产物不完整 build/backend/{runtime,code}"; exit 1
}

VERSION="$(sed -n 's/^__version__ = "\(.*\)"/\1/p' src/__init__.py)"
# 最终 .app 落到仓库上一级（X-iTrader/，与 X-iTrader-Server 等并列）便于分发；
# 拼装中间产物 build/backend 留在仓库内（已被 .gitignore 忽略）。
OUT_DIR="$(cd .. && pwd)"
APP="$OUT_DIR/X-iTrader-Client.app"
CONTENTS="$APP/Contents"

echo "==> 组装 $APP ($VERSION)"
rm -rf "$APP"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources"
cp "$BIN_PATH" "$CONTENTS/MacOS/X-iTrader-Client"
cp -R build/backend "$CONTENTS/Resources/backend"
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
# 逐文件签名用 `|| true`，签失败会静默漏过；整体复核一次，避免签坏的应用
# 拖到运行期才被 Gatekeeper 拦下（本地验证与 CI 发版走同一份脚本）
codesign --verify --strict "$APP"
echo "==> 签名校验通过"

echo "完成: $APP"
