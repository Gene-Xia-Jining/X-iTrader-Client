#!/usr/bin/env bash
#
# 将 ZCode 历史会话从 iTrader 迁移到 X-iTrader-Client
#
# 原理：ZCode 用 session 表的 directory / path / project_id 三列绑定会话与项目。
#       所有对话内容存在 message / part 表，按 session_id 关联，与路径无关。
#       因此只需更新 session 表这三列即可。
#
set -euo pipefail

# ── 路径定义
DB="$HOME/.zcode/cli/db/db.sqlite"
OLD_DIR="/Users/xiajining/Desktop/Dev/X-iTrader/iTrader"
NEW_DIR="/Users/xiajining/Desktop/Dev/X-iTrader/X-iTrader-Client"
OLD_PID="proj_users-xiajining-desktop-dev-x-itrader-itrader"
NEW_PID="proj_users-xiajining-desktop-dev-x-itrader-x-itrader-client"

# ── 前置检查
if [[ ! -f "$DB" ]]; then
  echo "✗ 数据库不存在: $DB" >&2
  exit 1
fi

if ! command -v sqlite3 &>/dev/null; then
  echo "✗ 需要 sqlite3 命令行工具" >&2
  exit 1
fi

# ── 备份
BACKUP="${DB}.bak.$(date +%Y%m%d_%H%M%S)"
cp "$DB" "$BACKUP"
echo "✓ 数据库已备份: $BACKUP"

# ── 迁移前统计
OLD_COUNT=$(sqlite3 "$DB" "SELECT COUNT(*) FROM session WHERE directory = '$OLD_DIR';")
NEW_COUNT=$(sqlite3 "$DB" "SELECT COUNT(*) FROM session WHERE directory = '$NEW_DIR';")
echo ""
echo "迁移前："
echo "  $OLD_DIR → $OLD_COUNT 个会话"
echo "  $NEW_DIR → $NEW_COUNT 个会话"

if [[ "$OLD_COUNT" -eq 0 ]]; then
  echo ""
  echo "✓ 旧项目下没有会话，无需迁移"
  exit 0
fi

# ── 执行迁移
echo ""
echo "开始迁移..."
sqlite3 "$DB" <<SQL
UPDATE session
SET directory = '$NEW_DIR',
    path      = '$NEW_DIR',
    project_id = '$NEW_PID'
WHERE directory = '$OLD_DIR';
SQL

# ── 迁移后验证
OLD_AFTER=$(sqlite3 "$DB" "SELECT COUNT(*) FROM session WHERE directory = '$OLD_DIR';")
NEW_AFTER=$(sqlite3 "$DB" "SELECT COUNT(*) FROM session WHERE directory = '$NEW_DIR';")

echo ""
echo "迁移后："
echo "  $OLD_DIR → $OLD_AFTER 个会话（应为 0）"
echo "  $NEW_DIR → $NEW_AFTER 个会话（应为 $((NEW_COUNT + OLD_COUNT))）"

if [[ "$OLD_AFTER" -eq 0 && "$NEW_AFTER" -eq $((NEW_COUNT + OLD_COUNT)) ]]; then
  echo ""
  echo "✓ 迁移成功"
  echo ""
  echo "下一步："
  echo "  1. 关闭 ZCode（如果已打开）"
  echo "  2. 重新进入 $NEW_DIR 打开 ZCode"
  echo "  3. 在历史会话列表中确认迁移的对话可见"
  echo ""
  echo "如需回滚："
  echo "  cp \"$BACKUP\" \"$DB\""
else
  echo ""
  echo "✗ 迁移结果与预期不符，请检查数据库" >&2
  echo "  回滚命令: cp \"$BACKUP\" \"$DB\"" >&2
  exit 1
fi
