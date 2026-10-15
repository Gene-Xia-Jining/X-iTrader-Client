#!/usr/bin/env bash
#
# 将 ZCode 历史会话从一个项目目录迁移到另一个（项目改名、移动后沿用旧对话）。
#
# 原理：ZCode 用 session 表的 directory / path 两列记录项目目录，并用 project_id
#       关联数据；project_id 由目录推导（proj_ + 目录小写、'/'→'-'、去掉字母数字
#       与 . _ - 之外的字符、去掉前导 '-'），例如
#       /Users/me/Dev/X-iTrader/iTrader → proj_users-me-dev-x-itrader-itrader
#       对话内容存在 message / part 等表，按 session_id 关联，与路径无关。
#       故只需更新 session 的这三列，以及其余含 project_id 列的表（脚本运行时
#       自动发现，当前为 session / permission / input_history）。
#
set -euo pipefail

usage() {
  cat <<'EOF'
用法: migrate_zcode_sessions.sh --old <旧目录> --new <新目录> [选项]

选项:
  --old <目录>    迁移前的项目目录（必填）
  --new <目录>    迁移后的项目目录（必填）
  --db <路径>     ZCode 数据库，默认 $HOME/.zcode/cli/db/db.sqlite
  -n, --dry-run   只显示将要迁移的数量，不写库
  -y, --yes       跳过交互确认（非交互环境必须显式指定）
  -h, --help      显示本帮助

示例:
  ./migrate_zcode_sessions.sh \
      --old /Users/me/Dev/X-iTrader/iTrader \
      --new /Users/me/Dev/X-iTrader/X-iTrader-Client

迁移前会自动备份数据库；如需回滚，把备份文件复制回原路径即可。
EOF
}

DB="$HOME/.zcode/cli/db/db.sqlite"
OLD_DIR=""
NEW_DIR=""
DRY_RUN=0
ASSUME_YES=0

# project_id 由目录路径推导，与 ZCode 内部规则一致（见文件头说明）
derive_project_id() {
  local s
  s=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
  s=$(printf '%s' "$s" | sed -e 's|/|-|g' -e 's|[^a-z0-9._-]||g' -e 's|^-*||')
  printf 'proj_%s' "$s"
}

# 单引号转义，供拼 SQL 字面量用
sql_lit() {
  printf "'%s'" "$(printf '%s' "$1" | sed "s/'/''/g")"
}

die() { echo "✗ $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --db)         DB="${2:?--db 需要一个参数}"; shift 2 ;;
    --old)        OLD_DIR="${2:?--old 需要一个参数}"; shift 2 ;;
    --new)        NEW_DIR="${2:?--new 需要一个参数}"; shift 2 ;;
    -n|--dry-run) DRY_RUN=1; shift ;;
    -y|--yes)     ASSUME_YES=1; shift ;;
    -h|--help)    usage; exit 0 ;;
    *) die "未知参数: $1（用 --help 查看用法）" ;;
  esac
done

[[ -n "$OLD_DIR" ]] || die "缺少 --old <旧目录>（用 --help 查看用法）"
[[ -n "$NEW_DIR" ]] || die "缺少 --new <新目录>（用 --help 查看用法）"

OLD_DIR="${OLD_DIR%/}"
NEW_DIR="${NEW_DIR%/}"
[[ "$OLD_DIR" != "$NEW_DIR" ]] || die "--old 与 --new 相同，无需迁移"

OLD_PID="$(derive_project_id "$OLD_DIR")"
NEW_PID="$(derive_project_id "$NEW_DIR")"
[[ "$OLD_PID" != "$NEW_PID" ]] || die "推导出的 project_id 相同（$OLD_PID），无法迁移"

# ── 前置检查
[[ -f "$DB" ]] || die "数据库不存在: $DB"
command -v sqlite3 >/dev/null 2>&1 || die "需要 sqlite3 命令行工具"

echo "数据库 : $DB"
echo "旧项目 : $OLD_DIR"
echo "         → project_id $OLD_PID"
echo "新项目 : $NEW_DIR"
echo "         → project_id $NEW_PID"
echo

# ── 发现除 session 外、所有含 project_id 列的表
PID_TABLES=$(sqlite3 "$DB" \
  "SELECT m.name FROM sqlite_master m JOIN pragma_table_info(m.name) p \
   WHERE m.type='table' AND p.name='project_id' AND m.name <> 'session';")

# ── 统计待迁移行数
S_OLD=$(sqlite3 "$DB" "SELECT COUNT(*) FROM session WHERE directory = $(sql_lit "$OLD_DIR");")
S_ALREADY=$(sqlite3 "$DB" "SELECT COUNT(*) FROM session WHERE directory = $(sql_lit "$NEW_DIR");")

echo "待迁移："
printf '  %-14s %s 条（directory = 旧目录）\n' "session" "$S_OLD"
TOTAL_PID=$S_OLD
for t in $PID_TABLES; do
  n=$(sqlite3 "$DB" "SELECT COUNT(*) FROM \"$t\" WHERE project_id = $(sql_lit "$OLD_PID");")
  printf '  %-14s %s 条（project_id = 旧 project_id）\n' "$t" "$n"
  TOTAL_PID=$((TOTAL_PID + n))
done
echo "新目录现有 session $S_ALREADY 条"
echo

if [[ "$TOTAL_PID" -eq 0 ]]; then
  echo "✓ 没有需要迁移的数据"
  exit 0
fi

if [[ "$DRY_RUN" -eq 1 ]]; then
  echo "（--dry-run：未写入任何变更）"
  exit 0
fi

if [[ "$ASSUME_YES" -eq 0 ]]; then
  [[ -t 0 ]] || die "非交互环境，请加 -y/--yes 确认执行"
  printf '确认执行迁移？[y/N] '
  read -r reply
  case "$reply" in
    [yY]|[yY][eE][sS]) ;;
    *) echo "已取消"; exit 0 ;;
  esac
fi

# ── 备份
BACKUP="${DB}.bak.$(date +%Y%m%d_%H%M%S)"
cp "$DB" "$BACKUP"
echo "✓ 已备份数据库: $BACKUP"

# ── 执行迁移（单事务，任一句失败则整体回滚）
echo "开始迁移..."
{
  echo "BEGIN;"
  echo "UPDATE session SET directory = $(sql_lit "$NEW_DIR"), path = $(sql_lit "$NEW_DIR"), project_id = $(sql_lit "$NEW_PID") WHERE directory = $(sql_lit "$OLD_DIR");"
  for t in $PID_TABLES; do
    echo "UPDATE \"$t\" SET project_id = $(sql_lit "$NEW_PID") WHERE project_id = $(sql_lit "$OLD_PID");"
  done
  echo "COMMIT;"
} | sqlite3 "$DB"

# ── 迁移后校验
S_AFTER=$(sqlite3 "$DB" "SELECT COUNT(*) FROM session WHERE directory = $(sql_lit "$OLD_DIR");")
S_NEW=$(sqlite3 "$DB" "SELECT COUNT(*) FROM session WHERE directory = $(sql_lit "$NEW_DIR");")
PID_AFTER=0
for t in $PID_TABLES; do
  n=$(sqlite3 "$DB" "SELECT COUNT(*) FROM \"$t\" WHERE project_id = $(sql_lit "$OLD_PID");")
  PID_AFTER=$((PID_AFTER + n))
done

echo
echo "迁移后："
echo "  旧目录 session      $S_AFTER 条（应为 0）"
echo "  新目录 session      $S_NEW 条（应为 $((S_ALREADY + S_OLD))）"
echo "  旧 project_id 残留  $PID_AFTER 条（应为 0）"

if [[ "$S_AFTER" -eq 0 && "$S_NEW" -eq $((S_ALREADY + S_OLD)) && "$PID_AFTER" -eq 0 ]]; then
  echo
  echo "✓ 迁移成功"
  echo
  echo "下一步："
  echo "  1. 关闭 ZCode（若已打开）"
  echo "  2. 重新进入 $NEW_DIR 打开 ZCode"
  echo "  3. 在历史会话列表中确认迁移的对话可见"
  echo
  echo "如需回滚: cp \"$BACKUP\" \"$DB\""
else
  echo
  echo "✗ 迁移结果与预期不符，请检查数据库" >&2
  echo "  回滚命令: cp \"$BACKUP\" \"$DB\"" >&2
  exit 1
fi
