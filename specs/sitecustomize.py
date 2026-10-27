"""包内 sitecustomize：提供 scipy.stats 的最小 stub，避免把 ~72M 的 scipy 装进包。

tqsdk/tafunc.py 顶层 `from scipy import stats`，但仅有的两处用法
（stats.norm.cdf / stats.norm.pdf）位于期权定价的私有函数里，客户端不会调用。
构建脚本装完依赖后删除 scipy，本文件由 Python 启动时自动执行补上等价实现。
只存在于打包产物（build.sh 拷入 site-packages）；开发态 .venv 装着真 scipy。

与服务端 specs/sitecustomize.py 同源：两端都只调 TqApi/TargetPosTask 的
下单与行情路径，不触发期权定价。
"""

import math
import sys
import types

try:
    import scipy.stats  # noqa: F401
except ImportError:
    import numpy as np

    class _Norm:
        """scipy.stats.norm 的最小替代（只用 cdf / pdf）。"""

        @staticmethod
        def cdf(x):
            arr = np.asarray(x, dtype=float)
            return 0.5 * (1.0 + np.vectorize(math.erf)(arr / math.sqrt(2.0)))

        @staticmethod
        def pdf(x):
            arr = np.asarray(x, dtype=float)
            return np.exp(-0.5 * arr * arr) / math.sqrt(2.0 * math.pi)

    stats = types.ModuleType("scipy.stats")
    stats.norm = _Norm()
    scipy = types.ModuleType("scipy")
    scipy.stats = stats
    sys.modules["scipy"] = scipy
    sys.modules["scipy.stats"] = stats
