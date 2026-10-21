"""后端日志落盘。

后端由 UI 壳 spawn，stdout/stderr 对用户不可见（mac 壳原先直接指向 /dev/null），
启动失败或运行异常只能靠复现。这里把三类来源合流到一个轮转文件：

- logging：各模块 logger（tqsdk、httpx、aiosqlite 等）
- print：启动、退出、遗留的调试输出
- 未捕获异常：主线程、子线程与 asyncio 的 traceback

业务日志（domain.events.LogEvent）由 BackendService 订阅后写入同一文件，
见 service._subscribe_events。

容量上限全部由标准库 RotatingFileHandler 控制：单文件 5MB，保留 5 份备份，
即 6 个文件、约 30MB 封顶，写满自动轮转，不需要任何外部清理。
"""

import io
import logging
import sys
import threading
from logging.handlers import RotatingFileHandler
from pathlib import Path

LOG_FILE_NAME = "backend.log"
MAX_BYTES = 5 * 1024 * 1024
BACKUP_COUNT = 5

# 这些 logger 的 INFO 级别是逐包/逐请求的流水，写进文件只会把有用信息冲掉，
# 保留 WARNING 及以上（连接失败、协议异常都在这一档）
_NOISY_PREFIXES = ("Tq", "tqsdk", "httpx", "httpcore", "urllib3", "websockets")

_logger = logging.getLogger("backend")
_configured = False


class _NoiseFilter(logging.Filter):
    def filter(self, record: logging.LogRecord) -> bool:
        if record.levelno <= logging.INFO and record.name.startswith(_NOISY_PREFIXES):
            return False
        return True


class _TeeStream:
    """把写入的文本按行转发给 logger，同时保留原流行为。

    按行转发而非按 write 调用：print 的多行内容与 traceback 都能保持一行一条，
    不会因为一次 write 里混了多行而破坏日志格式。

    转发本身必须吞掉异常——logger 的 handler 出错时会回写 sys.stderr，
    而 sys.stderr 此时就是这个对象，不隔离就会递归。
    """

    def __init__(self, original, logger: logging.Logger):
        self._original = original
        self._logger = logger
        self._buf = ""
        # 重入保护：文件 handler 自身出错时 logging 会回写 sys.stderr，而 sys.stderr
        # 就是本对象，不加锁就会再次进入 logger 并再次失败，直到爆栈
        self._local = threading.local()

    def write(self, text: str) -> int:
        if self._original is not None:
            try:
                self._original.write(text)
            except Exception:
                pass
        if getattr(self._local, "busy", False):
            return len(text)  # 日志链路内部回写，只落原流，不再转发
        self._buf += text
        while "\n" in self._buf:
            line, self._buf = self._buf.split("\n", 1)
            self._emit(line)
        return len(text)

    def flush(self) -> None:
        if self._buf:
            self._emit(self._buf)
            self._buf = ""
        if self._original is not None:
            try:
                self._original.flush()
            except Exception:
                pass

    def _emit(self, line: str) -> None:
        if not line.strip() or getattr(self._local, "busy", False):
            return
        self._local.busy = True
        try:
            self._logger.info(line)
        except Exception:
            pass
        finally:
            self._local.busy = False

    @property
    def encoding(self):
        return getattr(self._original, "encoding", "utf-8")

    @property
    def buffer(self):
        return getattr(self._original, "buffer", None)

    def isatty(self) -> bool:
        return False

    def fileno(self) -> int:
        # 部分三方库绕过 write 直接写 fd；有真实 fd 就透传，
        # 没有（如 pythonw 下为 None）时明确拒绝，而不是给出错误编号
        if self._original is None:
            raise io.UnsupportedOperation("fileno")
        return self._original.fileno()

    def writable(self) -> bool:
        return True

    def readable(self) -> bool:
        return False

    def seekable(self) -> bool:
        return False

    def passthrough(self, text: str) -> None:
        """只写原流、不转发给 logger。

        未捕获异常既要进文件（一条 CRITICAL），也要在开发态终端照原样显示；
        直接 write 会被转发成第二条日志，故提供这条旁路。
        """
        if self._original is None:
            return
        try:
            self._original.write(text)
            self._original.flush()
        except Exception:
            pass


def setup_logging(log_dir: Path, level: int = logging.INFO) -> Path:
    """配置文件日志并接管 stdout/stderr，返回日志文件路径。

    幂等：重复调用只返回路径，不会挂上第二个 handler（否则每次重启堆叠一份，
    同一条日志写多遍）。
    """
    global _configured
    path = log_dir / LOG_FILE_NAME
    if _configured:
        return path

    log_dir.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(
        path, maxBytes=MAX_BYTES, backupCount=BACKUP_COUNT, encoding="utf-8"
    )
    handler.setFormatter(
        logging.Formatter(
            "%(asctime)s %(levelname)-7s [%(threadName)s] %(name)s: %(message)s",
            datefmt="%Y-%m-%d %H:%M:%S",
        )
    )
    handler.addFilter(_NoiseFilter())

    root = logging.getLogger()
    root.setLevel(level)
    root.addHandler(handler)

    # pythonw / 无控制台场景下这两个流可能缺失，_TeeStream 已按 None 处理
    sys.stdout = _TeeStream(sys.stdout, logging.getLogger("stdout"))
    sys.stderr = _TeeStream(sys.stderr, logging.getLogger("stderr"))

    sys.excepthook = _log_uncaught
    threading.excepthook = _log_uncaught_thread

    _configured = True
    return path


def _echo_original_traceback(exc_type, exc, tb) -> None:
    """开发态在终端仍要看到常规 traceback（文件里已有结构化那份）。"""
    stream = sys.stderr
    passthrough = getattr(stream, "passthrough", None)
    if passthrough is None:
        return
    try:
        import traceback

        lines = traceback.format_exception(exc_type, exc, tb)
        passthrough("".join(lines))
    except Exception:
        pass


def _log_uncaught(exc_type, exc, tb) -> None:
    if issubclass(exc_type, KeyboardInterrupt):
        return  # 用户中断不是故障，不写 traceback
    _logger.critical("未捕获异常", exc_info=(exc_type, exc, tb))
    _echo_original_traceback(exc_type, exc, tb)


def _log_uncaught_thread(args) -> None:
    if issubclass(args.exc_type, SystemExit):
        return
    _logger.critical(
        "线程 %s 未捕获异常", getattr(args.thread, "name", "?"),
        exc_info=(args.exc_type, args.exc_value, args.exc_traceback),
    )
    _echo_original_traceback(args.exc_type, args.exc_value, args.exc_traceback)


def log_asyncio_exception(loop, context) -> None:
    """asyncio 的未处理异常不会走 sys.excepthook，必须单独挂。"""
    exc = context.get("exception")
    message = context.get("message", "asyncio 未处理异常")
    if exc is not None:
        _logger.error(message, exc_info=(type(exc), exc, exc.__traceback__))
    else:
        _logger.error(message)
