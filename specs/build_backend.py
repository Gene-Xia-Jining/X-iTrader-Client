"""把后端拼装成「嵌入式 Python 运行时 + 依赖 + 源码」的目录树，替代 PyInstaller。

产物布局（macOS / Windows 同构，只是相对 .app 或程序目录的位置不同）：

    backend/
      runtime/    python-build-standalone 运行时与全部第三方依赖
      code/       backend_entry.py + src/

为什么不用打包器：src 需要以普通目录外置以支持差量更新；PyInstaller 靠静态
分析猜依赖，历史上漏收过 plistlib / logging.handlers 导致后端启动即崩。
拼装方案下 src 是真实文件、依赖是真实 site-packages，不存在「猜」这一步。

入口不复制、不改名：壳直接调用 runtime 里的原版解释器并传入
code/backend_entry.py。改名副本在 Windows 上要连带处理 python3XX.dll 的
同目录查找，是没必要引入的风险面。

用法（由 build.sh / CI 调用）：
    python specs/build_backend.py --output build/backend

依赖 uv 与网络（首次下载运行时，之后复用 build/py-runtime 缓存）。
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SPECS = ROOT / "specs"

# Windows 上 stdout/stderr 默认按控制台代码页（cp1252）编码，本脚本的日志是中文，
# 第一次 print 就 UnicodeEncodeError 把构建打断。日志只求可见，统一按 UTF-8 输出。
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

PYTHON_VERSION = "3.14.3"

# 构建期只走国内镜像：PyPI 直连实测极慢（冷缓存首装 7 分钟）。可用环境变量覆盖。
DEFAULT_INDEX = "https://pypi.tuna.tsinghua.edu.cn/simple"


def log(msg: str) -> None:
    print(f"==> {msg}", flush=True)


def fail(msg: str) -> None:
    print(f"错误: {msg}", file=sys.stderr, flush=True)
    raise SystemExit(1)


def ensure_runtime(cache_dir: Path) -> Path:
    """返回 pbs 运行时根目录，必要时下载。

    UV_PYTHON_INSTALL_DIR 把运行时钉在仓库内（build/py-runtime），便于 CI 缓存，
    也不污染用户全局的 uv 运行时目录。
    """
    env = {**os.environ, "UV_PYTHON_INSTALL_DIR": str(cache_dir)}
    log(f"准备 Python {PYTHON_VERSION} 运行时（{cache_dir}）")
    subprocess.run(
        ["uv", "python", "install", PYTHON_VERSION],
        check=True,
        env=env,
    )
    found = subprocess.run(
        ["uv", "python", "find", PYTHON_VERSION],
        check=True,
        capture_output=True,
        text=True,
        env=env,
    )
    python = Path(found.stdout.strip())
    if not python.is_file():
        fail(f"uv python find 返回的解释器不存在: {python}")
    return python.parent.parent


def site_packages_of(runtime: Path) -> Path:
    """定位运行时的 site-packages。

    macOS/Linux 版是 lib/pythonX.Y/site-packages，Windows 版是 Lib/site-packages。
    """
    if os.name == "nt":
        path = runtime / "Lib" / "site-packages"
    else:
        matches = sorted(runtime.glob("lib/python3.*/site-packages"))
        if not matches:
            fail(f"运行时中找不到 site-packages: {runtime}")
        path = matches[-1]
    path.mkdir(parents=True, exist_ok=True)
    return path


def entry_python(runtime: Path) -> Path:
    """运行时中的解释器可执行文件（壳直接调用它，不改名不复制）。"""
    if os.name == "nt":
        return runtime / "python.exe"
    matches = sorted(runtime.glob("bin/python3.*"))
    matches = [p for p in matches if not p.name.endswith("-config") and p.is_file()]
    if not matches:
        fail(f"运行时中找不到解释器: {runtime}")
    return matches[-1]


def install_deps(runtime: Path, site_packages: Path) -> None:
    log("安装依赖（uv pip install --target）")
    python = entry_python(runtime)
    env = {**os.environ}
    env.setdefault("UV_DEFAULT_INDEX", DEFAULT_INDEX)
    subprocess.run(
        [
            "uv", "pip", "install",
            "-r", str(ROOT / "requirements.txt"),
            "--target", str(site_packages),
            "--python", str(python),
        ],
        check=True,
        env=env,
    )


def prune(runtime: Path, site_packages: Path) -> None:
    """裁剪运行时与依赖：只删运行时不需要的东西，不碰任何被 import 的模块。

    裁剪边界是实测出来的——numpy/typing、numpy/f2py 看似无用实则被 pandas
    导入链引用（删掉后 pandas 直接 ImportError），所以只清下面这些确定安全的：
    - scipy：由 sitecustomize 的 stub 替代（tqsdk 只在期权定价里用 stats）
    - 各包的 tests / __pycache__ / 类型桩
    - pip / 标准库里的构建与示例目录
    """
    log("裁剪运行时")

    # scipy 由 stub 替代。客户端只调 TqApi/TargetPosTask，不触发期权定价。
    for path in [site_packages / "scipy", *site_packages.glob("scipy-*.dist-info")]:
        shutil.rmtree(path, ignore_errors=True) if path.is_dir() else path.unlink(missing_ok=True)

    # 测试与缓存：不影响 import，体积可观（pandas/tests 单独就 17M）
    for pattern in ("tests", "__pycache__"):
        for path in site_packages.rglob(pattern):
            if path.is_dir():
                shutil.rmtree(path, ignore_errors=True)
    for path in site_packages.rglob("*.pyi"):
        path.unlink(missing_ok=True)

    # 运行时里的构建工具与示例
    py_xy = PYTHON_VERSION.rsplit(".", 1)[0]
    stdlib = runtime / "lib" / f"python{py_xy}" if os.name != "nt" else runtime / "Lib"
    for rel in ("ensurepip", "idlelib", "tkinter", "test", "turtledemo", "pydoc_data"):
        shutil.rmtree(stdlib / rel, ignore_errors=True)
    for rel in ("include", "tcl"):
        shutil.rmtree(runtime / rel, ignore_errors=True)
    if os.name != "nt":
        shutil.rmtree(stdlib / f"config-{py_xy}-darwin", ignore_errors=True)
        # bin/ 只留解释器本身：pip/idle/pydoc 等脚本在运行时里没有用途，
        # 且都带指向构建机路径的 shebang，换机器后会成悬空引用；
        # *-config 是编译扩展用的脚本，运行时用不到
        for path in (runtime / "bin").iterdir():
            if path.name.startswith("python") and not path.name.endswith("-config"):
                continue
            path.unlink(missing_ok=True)
    # site-packages 里 uv 顺带装进来的包管理器本身
    for name in ("pip", "setuptools", "wheel"):
        for path in [site_packages / name, *site_packages.glob(f"{name}-*.dist-info")]:
            if path.is_dir():
                shutil.rmtree(path, ignore_errors=True)
            else:
                path.unlink(missing_ok=True)


def copy_code(output: Path) -> None:
    """源码与资源：code/ 下与仓库根同构，backend_entry.py 据此补 sys.path。"""
    log("拷贝源码")
    code = output / "code"
    shutil.copytree(
        ROOT / "src",
        code / "src",
        ignore=shutil.ignore_patterns("__pycache__", "*.pyc", ".DS_Store"),
    )
    shutil.copy2(SPECS / "backend_entry.py", code / "backend_entry.py")


def copy_sitecustomize(site_packages: Path) -> None:
    shutil.copy2(SPECS / "sitecustomize.py", site_packages / "sitecustomize.py")


def dir_size(path: Path) -> str:
    total = sum(p.stat().st_size for p in path.rglob("*") if p.is_file())
    return f"{total / 1024 / 1024:.1f}M"


def verify(output: Path) -> None:
    """真实启动一次后端模块导入，确保拼装结果可用。

    只验证 import 链（tqsdk → pandas → scipy stub），不启动 RPC 服务：
    构建机不该占用端口，启动逻辑由打包后的实跑验证覆盖。
    """
    log("验证后端可导入")
    runtime = output / "runtime"
    python = entry_python(runtime)
    probe = (
        "import sys;"
        "sys.path.insert(0, sys.argv[1]);"
        "import src.backend.__main__;"
        "import tqsdk;"
        "assert 'scipy' in sys.modules;"
        "print('后端导入链 OK')"
    )
    result = subprocess.run(
        # -X utf8：-E 会屏蔽 PYTHONIOENCODING，只能靠命令行开关让子进程按 UTF-8 输出
        [str(python), "-E", "-s", "-X", "utf8", "-c", probe, str(output / "code")],
        capture_output=True,
        text=True,
        # 与子进程输出编码一致，避免把编码问题误报成「导入链验证失败」
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode != 0:
        print(result.stdout, file=sys.stderr)
        print(result.stderr, file=sys.stderr)
        fail("后端导入链验证失败")
    log(result.stdout.strip().splitlines()[-1])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", default=str(ROOT / "build" / "backend"))
    parser.add_argument("--runtime-cache", default=str(ROOT / "build" / "py-runtime"))
    parser.add_argument("--skip-verify", action="store_true")
    args = parser.parse_args()

    output = Path(args.output).resolve()
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)

    runtime_src = ensure_runtime(Path(args.runtime_cache).resolve())
    log(f"拷贝运行时到 {output / 'runtime'}")
    shutil.copytree(runtime_src, output / "runtime", symlinks=True)

    runtime = output / "runtime"
    site_packages = site_packages_of(runtime)
    install_deps(runtime, site_packages)
    prune(runtime, site_packages)
    copy_sitecustomize(site_packages)
    copy_code(output)

    if not args.skip_verify:
        verify(output)

    log(f"完成: {output} ({dir_size(output)})")


if __name__ == "__main__":
    main()
