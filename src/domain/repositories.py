from abc import ABC, abstractmethod
from typing import Optional

from .entities import TradeCommand, TradeCommandStatus

class TradeCommandRepository(ABC):
    @abstractmethod
    async def exists(self, account_id: str, command_id: str) -> bool: ...

    @abstractmethod
    async def add(self, command: TradeCommand) -> None: ...

    @abstractmethod
    async def get(self, account_id: str, command_id: str) -> Optional[TradeCommand]: ...

    @abstractmethod
    async def set_status(
        self,
        account_id: str,
        command_id: str,
        status: TradeCommandStatus,
        error: Optional[str] = None,
        order_id: Optional[str] = None,
    ) -> None: ...

class TokenStore(ABC):
    @abstractmethod
    def have_token(self) -> bool: ...

    @abstractmethod
    def load_token(self) -> Optional[str]: ...

    @abstractmethod
    def save_token(self, token: str) -> None: ...

    @abstractmethod
    def delete_token(self) -> None: ...

class StrategyStreamClient(ABC):
    @abstractmethod
    async def stream(self): ...


class TradeHistoryRepository(ABC):
    @abstractmethod
    def save_trades(self, account_id: str, trades: list[dict]) -> None: ...

    @abstractmethod
    def query_trades(
        self, account_id: str, date_range: str = "today", limit: int = 500
    ) -> list[dict]: ...

