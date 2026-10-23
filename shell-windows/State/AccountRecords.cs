using System.Text.Json.Nodes;

namespace XTrader.Shell.State;

// MARK: - 交易记录模型（协议字段见 src/backend/PROTOCOL.md 的 trading.records）

/// <summary>
/// 账户资金快照。TqSdk 未就绪的字段后端转 null，因此全部可空。
/// </summary>
public sealed class AccountSnapshot
{
    public double? Balance { get; init; }
    public double? Available { get; init; }
    public double? Margin { get; init; }
    public double? FloatProfit { get; init; }
    public double? PositionProfit { get; init; }
    public double? CloseProfit { get; init; }
    public double? Commission { get; init; }
    public double? RiskRatio { get; init; }
    public double? PreBalance { get; init; }
    public double? StaticBalance { get; init; }

    public static AccountSnapshot From(JsonObject? dict)
    {
        if (dict is null)
        {
            return new AccountSnapshot();
        }
        return new AccountSnapshot
        {
            Balance = JsonHelpers.Double(dict["balance"]),
            Available = JsonHelpers.Double(dict["available"]),
            Margin = JsonHelpers.Double(dict["margin"]),
            FloatProfit = JsonHelpers.Double(dict["float_profit"]),
            PositionProfit = JsonHelpers.Double(dict["position_profit"]),
            CloseProfit = JsonHelpers.Double(dict["close_profit"]),
            Commission = JsonHelpers.Double(dict["commission"]),
            RiskRatio = JsonHelpers.Double(dict["risk_ratio"]),
            PreBalance = JsonHelpers.Double(dict["pre_balance"]),
            StaticBalance = JsonHelpers.Double(dict["static_balance"]),
        };
    }
}

/// <summary>持仓行：一个合约的多空各成一行</summary>
public sealed class PositionRecord
{
    public string Symbol { get; init; } = "";
    public string Direction { get; init; } = "";
    public int Volume { get; init; }
    public double? OpenPrice { get; init; }
    public double? PositionPrice { get; init; }
    public double? LastPrice { get; init; }
    public double? FloatProfit { get; init; }
    public double? Margin { get; init; }

    public static PositionRecord? From(JsonObject dict)
    {
        if (dict["symbol"]?.GetValue<string>() is not { Length: > 0 } symbol)
        {
            return null;
        }
        return new PositionRecord
        {
            Symbol = symbol,
            Direction = dict["direction"]?.GetValue<string>() ?? "",
            Volume = JsonHelpers.Int(dict["volume"]) ?? 0,
            OpenPrice = JsonHelpers.Double(dict["open_price"]),
            PositionPrice = JsonHelpers.Double(dict["position_price"]),
            LastPrice = JsonHelpers.Double(dict["last_price"]),
            FloatProfit = JsonHelpers.Double(dict["float_profit"]),
            Margin = JsonHelpers.Double(dict["margin"]),
        };
    }
}

/// <summary>委托单行</summary>
public sealed class OrderRecord
{
    public string Id { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string DirectionText { get; init; } = "";
    public string OffsetText { get; init; } = "";
    public int VolumeOrigin { get; init; }
    public int VolumeLeft { get; init; }
    public double? LimitPrice { get; init; }
    public double? TradePrice { get; init; }
    public string StatusText { get; init; } = "";
    public string LastMsg { get; init; } = "";
    public string InsertTime { get; init; } = "";
    public string Source { get; init; } = "智能信号";

    public static OrderRecord? From(JsonObject dict)
    {
        if (dict["order_id"]?.GetValue<string>() is not { Length: > 0 } id)
        {
            return null;
        }
        return new OrderRecord
        {
            Id = id,
            Symbol = dict["symbol"]?.GetValue<string>() ?? "",
            DirectionText = dict["direction_text"]?.GetValue<string>() ?? "",
            OffsetText = dict["offset_text"]?.GetValue<string>() ?? "",
            VolumeOrigin = JsonHelpers.Int(dict["volume_orign"]) ?? 0,
            VolumeLeft = JsonHelpers.Int(dict["volume_left"]) ?? 0,
            LimitPrice = JsonHelpers.Double(dict["limit_price"]),
            TradePrice = JsonHelpers.Double(dict["trade_price"]),
            StatusText = dict["status_text"]?.GetValue<string>() ?? "",
            LastMsg = dict["last_msg"]?.GetValue<string>() ?? "",
            InsertTime = dict["insert_time"]?.GetValue<string>() ?? "",
            Source = dict["source"]?.GetValue<string>() ?? "智能信号",
        };
    }
}

/// <summary>成交行</summary>
public sealed class TradeRecord
{
    public string Id { get; init; } = "";
    public string OrderId { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string DirectionText { get; init; } = "";
    public string OffsetText { get; init; } = "";
    public double? Price { get; init; }
    public int Volume { get; init; }
    public string TradeTime { get; init; } = "";
    public double? RealizedProfit { get; init; }
    public double? Commission { get; init; }
    public string Source { get; init; } = "智能信号";

    public static TradeRecord? From(JsonObject dict)
    {
        if (dict["trade_id"]?.GetValue<string>() is not { Length: > 0 } id)
        {
            return null;
        }
        return new TradeRecord
        {
            Id = id,
            OrderId = dict["order_id"]?.GetValue<string>() ?? "",
            Symbol = dict["symbol"]?.GetValue<string>() ?? "",
            DirectionText = dict["direction_text"]?.GetValue<string>() ?? "",
            OffsetText = dict["offset_text"]?.GetValue<string>() ?? "",
            Price = JsonHelpers.Double(dict["price"]),
            Volume = JsonHelpers.Int(dict["volume"]) ?? 0,
            TradeTime = dict["trade_time"]?.GetValue<string>() ?? "",
            RealizedProfit = JsonHelpers.Double(dict["realized_profit"]),
            Commission = JsonHelpers.Double(dict["commission"]),
            Source = dict["source"]?.GetValue<string>() ?? "智能信号",
        };
    }
}


/// <summary>单个账户的交易记录快照（取数失败时 Error 非空，其余为空）</summary>
public sealed class AccountRecords
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public string Error { get; init; } = "";
    public AccountSnapshot Account { get; init; } = new();
    public IReadOnlyList<PositionRecord> Positions { get; init; } = [];
    public IReadOnlyList<OrderRecord> Orders { get; init; } = [];
    public IReadOnlyList<TradeRecord> Trades { get; init; } = [];

    public static AccountRecords? From(JsonObject dict)
    {
        if (dict["account_id"]?.GetValue<string>() is not { Length: > 0 } id)
        {
            return null;
        }
        return new AccountRecords
        {
            Id = id,
            Kind = dict["kind"]?.GetValue<string>() ?? "",
            Label = dict["label"]?.GetValue<string>() ?? id,
            Error = dict["error"]?.GetValue<string>() ?? "",
            Account = AccountSnapshot.From(dict["account"] as JsonObject),
            Positions = JsonHelpers.ObjectList(dict["positions"]).Select(PositionRecord.From).OfType<PositionRecord>().ToList(),
            Orders = JsonHelpers.ObjectList(dict["orders"]).Select(OrderRecord.From).OfType<OrderRecord>().ToList(),
            Trades = JsonHelpers.ObjectList(dict["trades"]).Select(TradeRecord.From).OfType<TradeRecord>().ToList(),
        };
    }
}
