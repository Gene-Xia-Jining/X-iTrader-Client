from abc import ABC, abstractmethod
from typing import Optional

from .entities import StrategySignal


class TradingExecutor(ABC):
    @abstractmethod
    async def set_target_position(self, symbol: str, target_position: int, account_id: str = "") -> None: ...

    @abstractmethod
    async def close(self) -> None: ...

    def trading_time(self, symbol: str, account_id: str = "") -> Optional[dict]:
        """取该合约的交易时段表（形如 {"day": [...], "night": [...]}）。

        返回 None 表示该实现拿不到时段表，调用方应放行而不是拦截：宁可偶尔
        漏拦（执行器自身有成交确认与超时兜底），也不要把开盘期间的正常调仓
        全部误拦。同步阻塞实现由调用方放到线程里执行。
        """
        return None

