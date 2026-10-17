"""GitHub Releases 自动更新基础设施。

更新源是本仓库的 GitHub Releases（CI 在 push main 时以 src/__init__.py 的
__version__ 打 tag 并发布）：

- iTrader-{version}-macos.dmg    全量包（内含 X-iTrader-Client.app，拖拽安装）
- iTrader-{version}-windows.zip  全量包（内含扁平程序目录，根级 X-iTrader.exe 壳
                                  + X-iTrader-Backend.exe 后端 + _internal/）
- iTrader-{version}-src.zip      补丁包（仅当相对上一版本只有 src/ 变化时发布，
                                  全平台共用；src 在打包时外置为普通目录，
                                  替换该目录即完成差量更新）

全量包两种格式都可解：macOS 用 dmg（hdiutil 挂载后复制 .app），Windows 用 zip；
补丁包恒为 zip。替换采用 rename→move→Popen→退出 模式：旧安装物整体改名让位，
新安装物移入原位置，启动新进程后旧进程退出；新进程启动时调用
cleanup_stale_backups() 清理 .old-* 残留。开发态（非 frozen）不做任何文件替换。
"""

import os
import plistlib
import shutil
import stat
import subprocess
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path

import httpx

GITHUB_REPO = "Gene-Xia-Jining/iTrader"
RELEASES_API_URL = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"
RELEASE_PAGE_URL = f"https://github.com/{GITHUB_REPO}/releases/latest"

# 当前平台全量包的 asset 名关键字与扩展名（CI 产物 iTrader-{version}-{平台}.dmg/.zip）。
# macOS 分发 dmg、Windows 分发 zip；扩展名参与匹配，避免把 .zip 全量包误当补丁包。
_FULL_ASSET_KEYWORDS = {"darwin": ("macos", ".dmg"), "win32": ("windows", ".zip")}
# 补丁包 asset 名关键字（iTrader-{version}-src.zip，恒为 zip）
_PATCH_ASSET_KEYWORD = "src"

# Windows 全量包根级的壳可执行文件名：程序目录（安装根）即它的父目录。
# 后端进程是 X-iTrader-Backend.exe，与壳同级；全量更新后重启的对象是壳。
_WIN_SHELL_EXE = "X-iTrader.exe"


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
    return bool(getattr(sys, "frozen", False))


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


async def fetch_latest_release(timeout: float = 10.0, transport=None) -> ReleaseInfo | None:
    """查询 GitHub 最新 Release。

    返回 None 表示无可更新内容（无 tag 或没有当前平台的全量包）；
    网络失败等错误直接抛出，由调用方处理。
    """
    async with httpx.AsyncClient(
        timeout=timeout, follow_redirects=True, transport=transport
    ) as client:
        resp = await client.get(
            RELEASES_API_URL, headers={"Accept": "application/vnd.github+json"}
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


async def download_release(url: str, dest_dir: Path, progress_cb=None) -> Path:
    """流式下载更新包到 dest_dir/update.<ext>，progress_cb(received, total) 汇报进度。

    扩展名取自 URL 路径（.dmg/.zip），使 extract_update 能按格式分派；无法识别
    时按 .zip 处理。取消由调用方取消协程实现：CancelledError 会在 await 点抛出，
    文件句柄随 with 块关闭，半成品由调用方清理暂存目录。
    """
    dest_dir.mkdir(parents=True, exist_ok=True)
    url_path = url.split("?", 1)[0]
    ext = ".dmg" if url_path.lower().endswith(".dmg") else ".zip"
    archive_path = dest_dir / f"update{ext}"
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
    finally:
        subprocess.run(
            ["hdiutil", "detach", mount_point, "-force"],
            capture_output=True, text=True,
        )
    return app_dst


def extract_update(archive_path: Path, stage_dir: Path) -> Path:
    """解压更新包到 stage_dir/stage，返回待安装物路径。

    按扩展名分派：.dmg 走 hdiutil 挂载提取（macOS 全量），.zip 走 zipfile。
    返回值按包类型分别是：mac 全量=X-iTrader-Client.app 目录、win 全量=程序目录
    （根级含 X-iTrader.exe）、补丁包=src 目录。zipfile 自带 CRC 校验。
    """
    extract_root = stage_dir / "stage"
    # 上次中断的残留暂存；清理失败只影响磁盘占用，后续解压会覆盖同名文件
    shutil.rmtree(extract_root, ignore_errors=True)
    extract_root.mkdir(parents=True, exist_ok=True)

    if archive_path.suffix.lower() == ".dmg":
        return _extract_dmg(archive_path, extract_root)

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


def app_install_root() -> Path | None:
    """当前安装物的根路径（全量更新的替换对象）：mac=X-iTrader-Client.app，win=程序目录。"""
    if not is_frozen():
        return None
    exe = Path(sys.executable).resolve()
    if sys.platform == "darwin":
        for parent in exe.parents:
            if parent.name == "X-iTrader-Client.app":
                return parent
        return None
    # windows onedir：exe 与 _internal 同级，所在目录即程序目录
    return exe.parent


def src_install_dir() -> Path | None:
    """打包时外置的 src 包目录（补丁更新的替换对象）。

    macOS bundle 中 Frameworks/src 是指向 Resources/src 的符号链接，
    解析到真实目录后再做替换，保证补丁落在数据实际所在处。
    """
    if not is_frozen():
        return None
    meipass = getattr(sys, "_MEIPASS", "")
    if not meipass:
        return None
    path = (Path(meipass) / "src").resolve()
    return path if path.is_dir() else None


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
        # 全量更新后重启的是壳（由壳再拉起同级的 X-iTrader-Backend.exe）
        binary = install_root / _WIN_SHELL_EXE
    if not binary.is_file():
        raise RuntimeError(f"更新包中未找到可执行文件: {binary}")
    return binary


def apply_update_and_restart(stage_item: Path, kind: str) -> Path:
    """安装已暂存的更新并启动新进程，返回新进程二进制路径。

    kind: "patch" 只替换外置 src 目录（二进制不变）；"full" 替换整个
    程序目录 / .app。调用方在返回后应尽快退出当前进程。
    开发态（非 frozen）一律拒绝，避免误改源码/虚拟环境。
    """
    if not is_frozen():
        raise RuntimeError("开发模式不支持自动安装，请从 GitHub Release 页面手动下载")

    if kind == "patch":
        target = src_install_dir()
        if target is None:
            raise RuntimeError("当前安装不支持差量更新，请下载完整包安装")
        _swap_directory(stage_item, target)
        binary = Path(sys.executable)
    else:
        target = app_install_root()
        if target is None:
            raise RuntimeError("无法定位当前安装位置")
        # swap 前先验证新包完整性（可执行文件存在），避免换上残缺包且无法回滚
        _launch_binary_of(stage_item)
        _swap_directory(stage_item, target)
        binary = _launch_binary_of(target)

    subprocess.Popen([str(binary)], cwd=str(binary.parent), close_fds=True)
    return binary


def cleanup_stale_backups() -> None:
    """清理上次更新遗留的 .old-* 备份（新进程启动时调用）。

    仅清理改名让位的旧安装物；删除失败（如被占用）不影响运行，属最佳努力。
    """
    if not is_frozen():
        return
    candidates: list[Path] = []
    if sys.platform == "darwin":
        app_root = app_install_root()
        if app_root is not None:
            candidates += list(app_root.parent.glob(f"{app_root.name}.old-*"))
    else:
        exe = Path(sys.executable)
        candidates += list(exe.parent.parent.glob(f"{exe.parent.name}.old-*"))
    src_root = src_install_dir()
    if src_root is not None:
        candidates += list(src_root.parent.glob(f"{src_root.name}.old-*"))
    for path in candidates:
        shutil.rmtree(path, ignore_errors=True)
