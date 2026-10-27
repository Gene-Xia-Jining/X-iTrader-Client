"""自动更新基础设施（本地目录 + GitHub Releases 双源）。

主源是本仓库的 GitHub Releases（CI 在 push main 时以 src/__init__.py 的
__version__ 打 tag 并发布）：

- X-iTrader-Client-{version}-Mac.dmg      全量包（内含 X-iTrader-Client.app，拖拽安装）
- X-iTrader-Client-{version}-Windows.zip  全量包（内含程序目录，根级 X-iTrader.exe 壳
                                          + backend/runtime/python.exe 后端运行时
                                          + backend/code/ 源码）
- x-itrader-client-{version}-src.zip      补丁包（仅当相对上一版本只有 src/ 变化时发布，
                                          全平台共用；src 在打包时外置为普通目录
                                          backend/code/src，替换该目录即完成差量更新）

本地源是同名安装包所在的目录（默认 DEFAULT_LOCAL_SOURCE，可用
ITRADER_UPDATE_SOURCE 覆盖）。检查更新时本地目录与 GitHub 并发查询，
取版本更高者、同版本优先本地；GitHub 失败（如匿名 API 403 限流）时回落到本地结果。
显式指定 source 时只查该源。

macOS 的本地源只认 build.sh 直接产出的 X-iTrader-Client.app 目录（版本取自其
Info.plist），**不扫 dmg**：dmg 是分发物，目录里留着的历史 dmg 会遮住刚打包的
新版本，导致检查更新误报「已是最新」。Windows 本地源仍为 zip。

全量包两种格式都可解：macOS 用 dmg（hdiutil 挂载后复制 .app），Windows 用 zip；
补丁包恒为 zip。替换采用 rename→move→Popen→退出 模式：旧安装物整体改名让位，
新安装物移入原位置，启动新进程后旧进程退出；新进程启动时调用
cleanup_stale_backups() 清理 .old-* 残留。开发态（未打包）仅允许全量替换
上级目录已安装的 .app。
"""

import asyncio
import os
import plistlib
import re
import shlex
import shutil
import stat
import subprocess
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path

import httpx

from .paths import is_packaged

GITHUB_REPO = "Gene-Xia-Jining/X-iTrader-Client"
RELEASES_API_URL = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"
RELEASE_PAGE_URL = f"https://github.com/{GITHUB_REPO}/releases/latest"

# 默认本地更新源目录（与服务端一致）：
# 非空时检查更新会同时扫描该目录与 GitHub Releases，取版本更高者；
# 置空字符串可关闭默认本地源。
DEFAULT_LOCAL_SOURCE = "/Users/xiajining/Desktop/Dev/X-iTrader"

# 当前平台全量包的 asset 名关键字与扩展名
# （CI 产物 X-iTrader-Client-{version}-Mac.dmg / -Windows.zip）。
# 匹配前 asset 名已转小写，故 macOS 关键字为 "mac"（对应 -Mac.dmg）。
# 扩展名参与匹配，避免把 .zip 全量包误当补丁包。
_FULL_ASSET_KEYWORDS = {"darwin": ("mac", ".dmg"), "win32": ("windows", ".zip")}
# 补丁包 asset 名关键字（x-itrader-client-{version}-src.zip，恒为 zip）
_PATCH_ASSET_KEYWORD = "src"

# Windows 全量包根级的壳可执行文件名：程序目录（安装根）即它的父目录。
# 后端在 backend\runtime\ 下（需要 runtime/ 与 code/ 子树），不再与壳同级；
# 全量更新后重启的对象是壳。
_WIN_SHELL_EXE = "X-iTrader.exe"

# macOS 本地打包产物目录：build.sh 直接产出的 .app 可当全量包用
_APP_SUFFIX = ".app"

# 差量更新由旧后端进程直接拉起新后端（壳不参与），新进程需要知道两件事：
# 1) 它不是壳的子进程，不能启用父进程监控——旧后端退出后它会判成孤儿而自杀；
# 2) 旧后端可能还没退干净，启动时要等它释放 backend.json 再接管。
# 只在 patch 分支设置：全量更新拉起的是壳，壳再 spawn 的后端是正常子进程。
RESTART_ENV = "ITRADER_RESTART_FROM_UPDATE"


@dataclass
class ReleaseInfo:
    version: str
    notes: str
    full_url: str
    patch_url: str = ""

    @property
    def has_patch(self) -> bool:
        return bool(self.patch_url)


def is_frozen() -> bool:
    """打包态判定的兼容别名（见 paths.is_packaged）。

    名字沿用旧称，但后端已不是 PyInstaller frozen 进程——sys.frozen 恒为
    False，判定改为读打包入口设置的环境标记。
    """
    return is_packaged()


def parse_version(text: str) -> tuple[int, ...] | None:
    """把 "0.0.2" / "v0.1" 解析成可比较的整数元组，非法格式返回 None。"""
    text = (text or "").strip().lstrip("vV")
    if not text:
        return None
    try:
        return tuple(int(part) for part in text.split("."))
    except ValueError:
        return None


def is_newer(remote: str, current: str) -> bool:
    """remote 版本是否严格高于 current；任一版本无法解析时视为不是新版本。"""
    remote_v = parse_version(remote)
    current_v = parse_version(current)
    if remote_v is None or current_v is None:
        return False
    return remote_v > current_v


def _select_assets(assets: list[dict]) -> tuple[str, str]:
    """从 Release assets 中选出当前平台全量包与补丁包的下载地址。

    全量包按 (关键字, 扩展名) 匹配（macOS=dmg / Windows=zip）；补丁包恒为 zip。
    扩展名过滤避免把全量包误判为补丁包（两者都含平台/版本片段）。
    """
    keyword, ext = _FULL_ASSET_KEYWORDS.get(sys.platform, ("", ""))
    full_url = ""
    patch_url = ""
    for asset in assets:
        name = str(asset.get("name", "")).lower()
        url = str(asset.get("browser_download_url", ""))
        is_zip = name.endswith(".zip")
        is_dmg = name.endswith(".dmg")
        if not name or not url or not (is_zip or is_dmg):
            continue
        if keyword and ext and name.endswith(ext) and keyword in name:
            full_url = url
        elif is_zip and _PATCH_ASSET_KEYWORD in name:
            patch_url = url
    return full_url, patch_url


def is_local_source(source: str) -> bool:
    """判断更新源是否为本地目录路径。"""
    s = (source or "").strip()
    if not s:
        return False
    if s.startswith("~") or s.startswith(("/", "./", "../", ".\\", "..\\")) or "\\" in s:
        return True
    try:
        p = Path(s).expanduser()
        if p.is_dir() or p.exists():
            return True
    except Exception:
        pass
    if "/" not in s:
        return True
    parts = s.split("/")
    if len(parts) != 2 or not all(p for p in parts):
        return True
    return False


def _app_bundle_version(app: Path) -> str:
    """读取 .app 的 CFBundleShortVersionString；读不到返回空串（视为不可用版本）。"""
    try:
        with (app / "Contents" / "Info.plist").open("rb") as f:
            data = plistlib.load(f)
    except (OSError, plistlib.InvalidFileException, ValueError):
        return ""
    return str(data.get("CFBundleShortVersionString") or "").strip()


def find_latest_in_folder(folder: Path) -> ReleaseInfo | None:
    """从本地目录中扫描最新的客户端更新包。

    全量包：macOS 只认 build.sh 直接产出的 .app 目录（版本取自其 Info.plist），
    不扫 dmg——目录里的历史 dmg 会遮住刚打包的新版本；Windows 匹配 .zip。
    补丁包：匹配含 src 的 .zip。
    """
    if not folder.exists() or not folder.is_dir():
        return None

    if sys.platform == "darwin":
        # 不扫 dmg：本地验证走 .app，dmg 只用于分发，混进来会让版本判断失真
        pattern_full = None
        pattern_app = re.compile(r"x-itrader-client.*\.app$", re.IGNORECASE)
    else:
        pattern_full = re.compile(
            r"x-itrader-client[_-]v?(\d+(?:\.\d+)+).*(?:windows)?.*\.zip$", re.IGNORECASE
        )
        pattern_app = None

    pattern_patch = re.compile(
        r"x-itrader-client[_-]v?(\d+(?:\.\d+)+).*src.*\.zip$", re.IGNORECASE
    )

    versions: dict[tuple[int, ...], tuple[str, Path | None, Path | None]] = {}

    for item in folder.iterdir():
        # .app 目录是 macOS 上唯一的本地全量包形态
        is_app = (
            pattern_app is not None
            and item.is_dir()
            and item.suffix.lower() == _APP_SUFFIX
            and pattern_app.search(item.name or "")
        )
        if not item.is_file() and not is_app:
            continue

        # 补丁包
        m_patch = pattern_patch.search(item.name) if item.is_file() else None
        if m_patch:
            ver_str = m_patch.group(1)
            parsed = parse_version(ver_str)
            if parsed is not None:
                curr_ver, curr_full, curr_patch = versions.get(
                    parsed, (ver_str, None, None)
                )
                versions[parsed] = (ver_str, curr_full, item)
                continue

        # 全量包
        if is_app:
            # 打包产物名固定为 X-iTrader-Client.app，版本只能取自 bundle 自身
            ver_str = _app_bundle_version(item)
        elif pattern_full is not None:
            m_full = pattern_full.search(item.name)
            if not m_full:
                continue
            ver_str = m_full.group(1)
        else:
            continue
        parsed = parse_version(ver_str)
        if parsed is not None:
            curr_ver, curr_full, curr_patch = versions.get(
                parsed, (ver_str, None, None)
            )
            versions[parsed] = (ver_str, item, curr_patch)

    if not versions:
        return None

    sorted_versions = sorted(versions.keys(), reverse=True)
    best_ver, best_full, best_patch = versions[sorted_versions[0]]
    if best_full is None and best_patch is None:
        return None

    full_url = str(best_full.resolve()) if best_full else ""
    patch_url = str(best_patch.resolve()) if best_patch else ""
    if not full_url and patch_url:
        full_url = patch_url

    return ReleaseInfo(
        version=best_ver,
        notes=f"本地安装包: {best_full.name if best_full else best_patch.name}",
        full_url=full_url,
        patch_url=patch_url,
    )


async def _fetch_github_release(
    repo: str, timeout: float = 10.0, transport=None
) -> ReleaseInfo | None:
    """查询 GitHub 最新 Release；无可用全量包时返回 None，网络错误直接抛出。"""
    api_url = f"https://api.github.com/repos/{repo}/releases/latest"
    async with httpx.AsyncClient(
        timeout=timeout, follow_redirects=True, transport=transport
    ) as client:
        resp = await client.get(
            api_url, headers={"Accept": "application/vnd.github+json"}
        )
        resp.raise_for_status()
        data = resp.json()

    version = str(data.get("tag_name", "")).strip()
    if not version:
        return None
    full_url, patch_url = _select_assets(data.get("assets", []))
    if not full_url:
        return None
    return ReleaseInfo(
        version=version,
        notes=str(data.get("body") or ""),
        full_url=full_url,
        patch_url=patch_url,
    )


def _pick_higher(local: ReleaseInfo | None, remote: ReleaseInfo | None) -> ReleaseInfo | None:
    """两路结果取版本更高者；同版本优先本地（本机包免下载）。

    版本无法解析时退回「有本地用本地，否则用远端」，避免把可用结果丢掉。
    """
    if local is None:
        return remote
    if remote is None:
        return local
    local_v = parse_version(local.version)
    remote_v = parse_version(remote.version)
    if local_v is None or remote_v is None:
        return local
    return remote if remote_v > local_v else local


async def fetch_latest_release(
    source: str = "", timeout: float = 10.0, transport=None
) -> ReleaseInfo | None:
    """查询最新 Release（支持本地目录与 GitHub Releases）。

    - 显式给定 source（入参或 ITRADER_UPDATE_SOURCE）时只查该源：本地目录即扫描目录，
      owner/repo 即查该仓库。
    - 未显式指定时同时查默认本地目录与 GITHUB_REPO，取版本更高者（同版本优先本地）；
      GitHub 请求失败时若本地有结果就返回本地，两路都没有才抛出网络错误。
    """
    src = (source or os.environ.get("ITRADER_UPDATE_SOURCE", "")).strip()
    if src:
        if not is_local_source(src):
            return await _fetch_github_release(src, timeout, transport)
        folder = Path(src).expanduser().resolve()
        if not folder.is_dir():
            raise FileNotFoundError(f"本地更新目录不存在: {folder}")
        return find_latest_in_folder(folder)

    local_task = None
    default_dir = (DEFAULT_LOCAL_SOURCE or "").strip()
    if default_dir:
        default_p = Path(default_dir).expanduser()
        if default_p.is_dir():
            local_task = asyncio.to_thread(find_latest_in_folder, default_p)

    remote_task = _fetch_github_release(GITHUB_REPO, timeout, transport)
    if local_task is None:
        return await remote_task

    # 两路并发：本地扫描几乎瞬时，不等 GitHub 超时就能拿到结果
    local_release, remote_result = await asyncio.gather(
        local_task, remote_task, return_exceptions=True
    )
    if isinstance(local_release, BaseException):
        local_release = None
    if isinstance(remote_result, BaseException):
        # 本地有可用包时不让网络故障挡住更新；两路皆空才把错误抛给调用方
        if local_release is None:
            raise remote_result
        return local_release
    return _pick_higher(local_release, remote_result)


def _copy_app_bundle(src: Path, dst: Path) -> None:
    """复制 .app 目录到 dst。

    必须用 ditto 而不是 shutil.copytree：Python.framework 内部大量符号链接，
    ditto 完整保留链接与扩展属性，ad-hoc 签名在复制后仍然有效。
    """
    if dst.exists():
        shutil.rmtree(dst, ignore_errors=True)
    subprocess.run(["ditto", str(src), str(dst)], check=True)


async def download_release(url: str, dest_dir: Path, progress_cb=None) -> Path:
    """下载更新包（支持本地文件/本地 .app 目录与网络下载）。"""
    dest_dir.mkdir(parents=True, exist_ok=True)
    url_path = url.split("?", 1)[0]
    if url_path.lower().endswith(_APP_SUFFIX):
        ext = _APP_SUFFIX
    else:
        ext = ".dmg" if url_path.lower().endswith(".dmg") else ".zip"
    archive_path = dest_dir / f"update{ext}"

    local_path = Path(url)
    if local_path.is_dir() and local_path.suffix.lower() == _APP_SUFFIX:
        # 本地 .app 直接当全量包：复制一份到暂存区，避免安装时源目录被就地改动
        await asyncio.to_thread(_copy_app_bundle, local_path, archive_path)
        if progress_cb is not None:
            progress_cb(1, 1)
        return archive_path

    if local_path.is_file():
        await asyncio.to_thread(shutil.copy2, str(local_path), str(archive_path))
        if progress_cb is not None:
            progress_cb(1, 1)
        return archive_path

    # 下载大文件：connect/pool 短超时，read 允许单个 chunk 间隔最长 60s
    timeout = httpx.Timeout(connect=10.0, read=60.0, write=60.0, pool=10.0)
    async with httpx.AsyncClient(timeout=timeout, follow_redirects=True) as client:
        async with client.stream("GET", url) as resp:
            resp.raise_for_status()
            total = int(resp.headers.get("content-length") or 0)
            received = 0
            with archive_path.open("wb") as f:
                async for chunk in resp.aiter_bytes(65536):
                    f.write(chunk)
                    received += len(chunk)
                    if progress_cb is not None:
                        progress_cb(received, total)
    return archive_path


def _is_mac_full_zip(names: list[str]) -> bool:
    return any(name == "X-iTrader-Client.app/" or name.startswith("X-iTrader-Client.app/") for name in names)


def _is_patch_zip(names: list[str]) -> bool:
    top = {name.split("/")[0] for name in names}
    return "src" in top and "X-iTrader-Client.app" not in top and _WIN_SHELL_EXE not in top


def _extract_dmg(dmg_path: Path, extract_root: Path) -> Path:
    """挂载 dmg、用 ditto 复制出 X-iTrader-Client.app、卸载，返回 .app 路径。

    用 hdiutil attach + ditto 而非 cp -R：ditto 完整保留符号链接（Python.framework
    内部大量使用）与扩展属性，复制后 ad-hoc 签名仍有效。挂载点由 hdiutil 决定
    （/Volumes/<volname>，重名时加后缀），从输出解析而非硬编码。始终 detach，
    异常路径也不残留挂载。
    """
    attach = subprocess.run(
        ["hdiutil", "attach", "-nobrowse", "-readonly", "-plist", str(dmg_path)],
        capture_output=True, text=True, check=True,
    )
    # -plist 输出里取挂载点：<key>mount-point</key><string>/Volumes/...</string>
    # plistlib.loads 同时接受 str/bytes，故不依赖 text= 的返回类型
    info = plistlib.loads(attach.stdout)
    mount_point = ""
    for entity in info.get("system-entities", []):
        if entity.get("mount-point"):
            mount_point = entity["mount-point"]
            break
    if not mount_point:
        raise ValueError("dmg 挂载失败：未找到挂载点")

    try:
        mount = Path(mount_point)
        app_src = mount / "X-iTrader-Client.app"
        if not app_src.is_dir():
            raise ValueError("dmg 中未找到 X-iTrader-Client.app")
        app_dst = extract_root / "X-iTrader-Client.app"
        subprocess.run(["ditto", str(app_src), str(app_dst)], check=True)
        # 移除可能从下载 dmg 继承的 quarantine 隔离属性，避免 Gatekeeper 阻止自启动
        subprocess.run(
            ["xattr", "-r", "-d", "com.apple.quarantine", str(app_dst)],
            capture_output=True,
        )
    finally:
        subprocess.run(
            ["hdiutil", "detach", mount_point, "-force"],
            capture_output=True, text=True,
        )
    return app_dst


def extract_update(archive_path: Path, stage_dir: Path) -> Path:
    """解压更新包到 stage_dir/stage，返回待安装物路径。

    按扩展名分派：.dmg 走 hdiutil 挂载提取（macOS 全量），.app 是本地打包产物
    直接改名就位，.zip 走 zipfile。返回值按包类型分别是：mac 全量=X-iTrader-Client.app
    目录、win 全量=程序目录（根级含 X-iTrader.exe）、补丁包=src 目录。
    zipfile 自带 CRC 校验。
    """
    extract_root = stage_dir / "stage"
    # 上次中断的残留暂存；清理失败只影响磁盘占用，后续解压会覆盖同名文件
    shutil.rmtree(extract_root, ignore_errors=True)
    extract_root.mkdir(parents=True, exist_ok=True)

    if archive_path.suffix.lower() == ".dmg":
        return _extract_dmg(archive_path, extract_root)

    if archive_path.suffix.lower() == _APP_SUFFIX and archive_path.is_dir():
        # 暂存区里的 .app 是 download_release 已 ditto 复制好的副本，移到舞台
        # 直接改名（同一分区），避免再复制一次几十 MB 的 bundle
        app_dst = extract_root / "X-iTrader-Client.app"
        shutil.move(str(archive_path), str(app_dst))
        return app_dst

    extract_root_res = extract_root.resolve()
    with zipfile.ZipFile(archive_path) as zf:
        names = zf.namelist()
        if not (_is_mac_full_zip(names) or _is_patch_zip(names)):
            if _WIN_SHELL_EXE not in names and f"{_WIN_SHELL_EXE}/" not in names:
                raise ValueError("压缩包内容不是有效的 iTrader 更新包")
        # 安全解压：校验每个条目路径，防止 Zip Slip 路径穿越
        for member in zf.infolist():
            target_path = (extract_root / member.filename).resolve()
            if not (target_path == extract_root_res or extract_root_res in target_path.parents):
                raise ValueError(f"压缩包包含非法路径条目: {member.filename}")
        zf.extractall(extract_root)
        # CI 用 zip -ry 打包，可能含符号链接；zipfile 会把链接写成普通文件，需还原
        for info in zf.infolist():
            if stat.S_ISLNK((info.external_attr >> 16) & 0xFFFF):
                link = extract_root / info.filename
                if link.is_file():
                    link_path = zf.read(info).decode("utf-8", "replace")
                    try:
                        link.unlink()
                        os.symlink(link_path, link)
                    except OSError:
                        # Windows 上非管理员权限无 symlink 特权，降级忽略
                        pass

    if _is_mac_full_zip(names):
        item = extract_root / "X-iTrader-Client.app"
    elif _is_patch_zip(names):
        item = extract_root / "src"
    else:
        item = extract_root
    if not item.is_dir():
        raise ValueError(f"压缩包中未找到预期的内容: {item.name}")
    return item


def backend_root() -> Path | None:
    """后端目录（内含 runtime/ 与 code/），由当前解释器位置推导。

    打包态的布局是固定的两层：

        backend/runtime/python.exe            Windows 解释器
        backend/runtime/bin/python3.14        macOS 解释器
        backend/code/backend_entry.py
        backend/code/src/

    从入口向上找同时含 runtime/ 与 code/ 的目录，比按层数硬编码 parents[n]
    更稳：mac 是 bin/ 下一层、win 是 runtime/ 下一层，两者不相同。
    """
    if not is_packaged():
        return None
    exe = Path(sys.executable).resolve()
    for parent in exe.parents:
        if (parent / "runtime").is_dir() and (parent / "code").is_dir():
            return parent
    return None


def app_install_root() -> Path | None:
    """当前安装物的根路径（全量更新的替换对象）：mac=X-iTrader-Client.app，win=程序目录。"""
    if not is_packaged():
        return None
    exe = Path(sys.executable).resolve()
    if sys.platform == "darwin":
        for parent in exe.parents:
            if parent.name == "X-iTrader-Client.app":
                return parent
        return None
    # Windows 扁平布局：安装根是含壳 X-iTrader.exe 的目录。后端不再与壳同级
    # （它需要 runtime/ 子树），因此按壳文件定位而不是取 exe 的父目录。
    for parent in exe.parents:
        if (parent / _WIN_SHELL_EXE).is_file():
            return parent
    return None


def src_install_dir() -> Path | None:
    """外置的 src 包目录（补丁更新的替换对象）。"""
    root = backend_root()
    if root is None:
        return None
    path = (root / "code" / "src").resolve()
    return path if path.is_dir() else None


def _backend_command() -> list[str]:
    """拉起后端的完整命令行（解释器 + 入口脚本）。

    pbs 布局下后端是「嵌入式解释器 + 入口脚本」，不是单个可执行文件；
    -E -s 隔离环境变量与 user site，保证只认包内 site-packages。
    """
    root = backend_root()
    if root is None:
        raise RuntimeError("无法定位后端目录（runtime/ 与 code/）")
    return [str(Path(sys.executable)), "-E", "-s", str(root / "code" / "backend_entry.py")]


def _swap_directory(new_item: Path, target: Path) -> None:
    """把 target 原子性地换成 new_item：旧目录改名让位，新目录移入原位置。

    失败时把旧目录改回原名再抛错，保证原安装可用。
    """
    backup = target.with_name(f"{target.name}.old-{os.getpid()}")
    shutil.rmtree(backup, ignore_errors=True)  # 清掉上次替换中断的残留备份
    target.rename(backup)
    try:
        shutil.move(str(new_item), str(target))
    except Exception:
        if target.exists():
            shutil.rmtree(target, ignore_errors=True)
        if backup.exists():
            backup.rename(target)
        raise


def _launch_binary_of(install_root: Path) -> Path:
    if sys.platform == "darwin":
        binary = install_root / "Contents" / "MacOS" / "X-iTrader-Client"
    else:
        # Windows 扁平布局：安装根（后端 exe 的父目录）根级是壳 X-iTrader.exe，
        # 全量更新后重启的是壳（由壳再拉起 backend\runtime 下的后端入口）
        binary = install_root / _WIN_SHELL_EXE
    if not binary.is_file():
        raise RuntimeError(f"更新包中未找到可执行文件: {binary}")
    return binary


def _relaunch_full_app(target: Path, binary: Path) -> None:
    """脱离当前进程树拉起新版本的完整应用（壳进程）。

    macOS: target 是 X-iTrader-Client.app 目录。
    必须通过 /usr/bin/open -n <app> 启动以获得合法的 LaunchServices GUI 上下文；
    必须延时 2 秒（sleep 2）等旧壳与旧后端完成退出收尾（旧壳收到 stopping 事件后有
    1.5s 退出延迟），避免同 bundle、同端口、同 backend.json 发生重叠冲突；
    必须使用 start_new_session=True 脱离当前进程组与 session，避免被 launchd
    销毁旧 App service 时连坐清理。

    Windows: target 是程序根目录，binary 是根级 X-iTrader.exe。
    使用 cmd.exe 延时 2 秒拉起新壳并脱离当前进程树。
    """
    if sys.platform == "darwin":
        cmd = f"sleep 2 && /usr/bin/open -n {shlex.quote(str(target))}"
        subprocess.Popen(
            ["/bin/sh", "-c", cmd],
            start_new_session=True,
            close_fds=True,
        )
    elif sys.platform == "win32":
        cmd = f'ping 127.0.0.1 -n 3 >nul & start "" "{binary}"'
        creationflags = (
            getattr(subprocess, "DETACHED_PROCESS", 0)
            | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
        )
        subprocess.Popen(
            cmd,
            shell=True,
            close_fds=True,
            creationflags=creationflags,
        )
    else:
        subprocess.Popen(
            [str(binary)],
            cwd=str(binary.parent),
            close_fds=True,
            start_new_session=True,
        )


def apply_update_and_restart(stage_item: Path, kind: str) -> Path:
    """安装已暂存的更新并启动新进程，返回新进程二进制路径。

    kind: "patch" 只替换外置 src 目录（二进制不变）；"full" 替换整个
    程序目录 / .app。调用方在返回后应尽快退出当前进程。
    开发态（非打包）默认拒绝，避免误改源码/虚拟环境；全量更新时若能唯一定位
    上级目录已安装的 .app 则允许替换。
    """
    if not is_packaged():
        if kind == "full":
            candidate = Path(__file__).resolve().parents[3] / "X-iTrader-Client.app"
            if candidate.is_dir():
                target = candidate
                _launch_binary_of(stage_item)
                _swap_directory(stage_item, target)
                binary = _launch_binary_of(target)
                _relaunch_full_app(target, binary)
                return binary
        raise RuntimeError("开发模式不支持自动安装，请从 GitHub Release 页面手动下载")

    if kind == "patch":
        target = src_install_dir()
        if target is None:
            raise RuntimeError("当前安装不支持差量更新，请下载完整包安装")
        _swap_directory(stage_item, target)
        binary = Path(sys.executable)
        # 后端进程直接拉起的是「另一个后端」，不是 shell：标记 env 让新进程
        # 跳过父进程监控（旧后端马上会退出），并等待 backend.json 释放后再接管
        env = {**os.environ, RESTART_ENV: "1"}
        subprocess.Popen(
            _backend_command(),
            cwd=str(binary.parent),
            close_fds=True,
            env=env,
        )
        return binary

    target = app_install_root()
    if target is None:
        raise RuntimeError("无法定位当前安装位置")
    # swap 前先验证新包完整性（可执行文件存在），避免换上残缺包且无法回滚
    _launch_binary_of(stage_item)
    _swap_directory(stage_item, target)
    binary = _launch_binary_of(target)

    _relaunch_full_app(target, binary)
    return binary


def cleanup_stale_backups() -> None:
    """清理上次更新遗留的 .old-* 备份（新进程启动时调用）。

    仅清理改名让位的旧安装物；删除失败（如被占用）不影响运行，属最佳努力。
    """
    if not is_packaged():
        return
    candidates: list[Path] = []
    app_root = app_install_root()
    if app_root is not None:
        candidates += list(app_root.parent.glob(f"{app_root.name}.old-*"))
    src_root = src_install_dir()
    if src_root is not None:
        candidates += list(src_root.parent.glob(f"{src_root.name}.old-*"))
    for path in candidates:
        shutil.rmtree(path, ignore_errors=True)
