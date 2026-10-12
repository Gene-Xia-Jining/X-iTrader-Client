from enum import Enum
from dataclasses import dataclass, field
from typing import Optional

class TradeCommandStatus(str, Enum):
    PENDING = "pending"
    EXECUTING = "executing"
    EXECUTED = "executed"
    FAILED = "failed"


class AccountKind(str, Enum):
    SIM = "sim"
    LIVE = "live"


@dataclass
class Account:
    """一个可独立运行的交易账户。

    kind="sim" 使用快期模拟账户（TqKq），kind="live" 使用期货公司实盘账户（TqAccount）。
    tq_account/tq_password 是快期权限账户（TqAuth），两种 kind 都要。
    symbols 是账户级品种（模拟与实盘各自独立选择）。
    strategy 是账户选择的交易策略 id（服务器 /api/strategies 目录中的 id，空 = 未选择）。

    模拟账户全局仅一个（快期多模拟账户需专业版，暂不支持）；
    实盘账户可多个，每个独立 TqApi 会话运行。
    """

    id: str
    kind: str
    label: str
    tq_account: str
    tq_password: str
    broker: str = ""
    trade_account: str = ""
    trade_password: str = ""
    symbols: list[str] = field(default_factory=list)
    strategy: str = ""
    enabled: bool = True


@dataclass
class StrategySignal:
    id: str
    symbol: str
    side: Optional[str]
    price: float
    position: int
    updated_at: int


@dataclass
class TradeCommand:
    account_id: str
    id: str
    symbol: str
    side: Optional[str]
    price: float
    target_position: int
    received_at: int
    status: TradeCommandStatus = TradeCommandStatus.PENDING
    executed_at: Optional[int] = None
    order_id: Optional[str] = None
    error: Optional[str] = None


@dataclass
class TradingConfiguration:
    """全局配置（与账户无关的项）。账户本身在 accounts 表中，见 Account。"""

    server_url: str
    auto_trade: bool
    proxy_url: str = ""
    auto_check_update: bool = True
    skipped_version: str = ""
    database_path: str = "data/client.db"
