#!/usr/bin/env bash
# 把 build.sh 产出的 X-iTrader-Client.app 封装成可分发的 dmg。
# 产物落在仓库上一级（X-iTrader/），与 build.sh 的 .app 并列，便于直接分发。
#
# dmg 内为拖拽安装布局：X-iTrader-Client.app + /Applications 快捷方式。
# 用 ditto 复制 .app：保留符号链接（Python.framework 大量使用）与扩展属性，
# 复制后 ad-hoc 签名对 dmg 内的 .app 仍有效。
#
# 用法: ./build_dmg.sh   （需先运行 ./build.sh 生成 .app）
set -euo pipefail
cd "$(dirname "$0")"

OUT_DIR="$(cd .. && pwd)"
APP="$OUT_DIR/X-iTrader-Client.app"
[ -d "$APP" ] || {
    echo "错误: 未找到 $APP，请先运行 ./build.sh"
    exit 1
}

VERSION="$(sed -n 's/^__version__ = "\(.*\)"/\1/p' src/__init__.py)"
# 文件名与 CI Release asset 一致（updater 按 "mac" + ".dmg" 匹配全量包）
DMG="$OUT_DIR/X-iTrader-Client-$VERSION-Mac.dmg"

echo "==> 封装 $DMG ($VERSION)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
ditto "$APP" "$STAGE/X-iTrader-Client.app"
ln -s /Applications "$STAGE/Applications"

rm -f "$DMG"
# UDZO = 压缩只读镜像，适合分发
hdiutil create \
    -volname "X-iTrader-Client" \
    -srcfolder "$STAGE" \
    -ov -format UDZO \
    "$DMG" >/dev/null

echo "完成: $DMG"
