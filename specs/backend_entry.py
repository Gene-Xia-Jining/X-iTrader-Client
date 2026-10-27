"""生产启动入口：由壳用包内嵌入式解释器执行。

后端是 python-build-standalone 运行时 + site-packages 依赖 + code/ 源码的
拼装产物（见 build.sh），不再有打包器生成的 frozen 可执行文件。壳的调用形态是：

    <backend>/runtime/bin/python3.14 -E -s <backend>/code/backend_entry.py

本文件只做两件事：把 code/ 放进模块搜索路径（使 `import src` 可解析），
然后以完整包路径导入 src.backend.__main__ 并运行。数据根 chdir、日志等
在 __main__ 里统一处理，与开发态走同一条代码路径。

包内布局（macOS 与 Windows 同构，仅相对 .app / 程序目录的位置不同）：

    backend/
      runtime/   嵌入式 Python 与全部依赖
      code/      本文件 + src/ 源码
"""

import os
import sys
from pathlib import Path

# 壳用 -E 启动（忽略 PYTHONPATH 等环境变量），搜索路径只能在这里补：
# code/ 的上一级是 backend/，而 `import src` 需要 code/ 本身在 path 上
_HERE = Path(__file__).resolve().parent
if str(_HERE) not in sys.path:
    sys.path.insert(0, str(_HERE))


def main() -> None:
    # 打包标记：替代 sys.frozen 判定（嵌入运行时不是 frozen 进程）
    os.environ["ITRADER_PACKAGED"] = "1"
    import importlib

    importlib.import_module("src.backend.__main__").main()


if __name__ == "__main__":
    main()
