"""PyInstaller 入口：等价于 `python -m src.backend`。

src/backend/__main__.py 含包内相对导入（from .rpc import ...），不能作为
PyInstaller 顶层脚本静态分析；由这里以完整包路径导入后调用 main()。

必须用 importlib 动态导入：src 以源码目录随包外置（支持差分更新，见
specs/backend.spec），任何对 src 的静态 import（含函数内、条件分支内）都会
让 PyInstaller 把它收进 PYZ，而 PYZ 优先级高于外置目录，使外置退回失效。
"""

import importlib


def main() -> None:
    importlib.import_module("src.backend.__main__").main()


if __name__ == "__main__":
    main()
