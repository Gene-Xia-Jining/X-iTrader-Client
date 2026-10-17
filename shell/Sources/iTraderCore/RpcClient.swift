import Foundation
import Network

public enum RpcError: LocalizedError {
    case handshakeFailed(String)
    case connectionClosed
    /// error 消息, 服务端异常类型名, 结构化附加数据（响应里的 data 字段）
    case requestFailed(String, String, [String: Any])

    public var errorDescription: String? {
        switch self {
        case .handshakeFailed(let msg):
            return "后端握手失败: \(msg)"
        case .connectionClosed:
            return "与后端的连接已断开"
        case .requestFailed(let error, let type, _):
            return type == "RuntimeError" || type == "ValueError" ? error : "\(type): \(error)"
        }
    }

    /// 启动前预检问题列表（EngineStartBlockedError 的 data.issues）。
    /// 其他失败返回空数组。
    public var issues: [String] {
        guard case .requestFailed(_, let type, let data) = self,
              type == "EngineStartBlockedError" else { return [] }
        return data["issues"] as? [String] ?? []
    }
}

/// 一次性标记：NWConnection 状态回调可能在多状态间触发，resume 只执行一次。
private final class OnceBox: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false

    func tryMark() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if done { return false }
        done = true
        return true
    }
}

/// 本地 RPC 客户端：127.0.0.1 TCP + NDJSON，协议见 src/backend/PROTOCOL.md。
///
/// 响应与事件共用一条连接：按「有 event 键 → 事件；有 id 键 → 响应」分派。
/// 请求不设客户端超时——各端点在服务端均有界（网络探测 5s、TqApi 登录数秒、
/// 更新下载走事件推送），断连时所有挂起请求统一失败。
public actor RpcClient {
    public typealias EventHandler = @Sendable (_ name: String, _ data: [String: Any]) -> Void
    public typealias DisconnectHandler = @Sendable () -> Void

    private let port: UInt16
    private let token: String
    private var connection: NWConnection?
    private var buffer = Data()
    private var nextID = 1
    private var pending: [Int: CheckedContinuation<[String: Any], Error>] = [:]
    private var eventHandler: EventHandler?
    private var disconnectHandler: DisconnectHandler?
    private var handshakeContinuation: CheckedContinuation<Void, Error>?
    private var closed = false

    public init(info: BackendInfo) {
        self.port = UInt16(info.port)
        self.token = info.token
    }

    public func setHandlers(event: EventHandler?, disconnect: DisconnectHandler?) {
        self.eventHandler = event
        self.disconnectHandler = disconnect
    }

    // -------- 连接与握手 --------

    public func connect() async throws {
        guard connection == nil else { return }
        guard let endpointPort = NWEndpoint.Port(rawValue: port) else {
            throw RpcError.handshakeFailed("非法端口 \(port)")
        }
        let conn = NWConnection(host: "127.0.0.1", port: endpointPort, using: .tcp)
        connection = conn
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let resumed = OnceBox()
            conn.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if resumed.tryMark() { cont.resume() }
                case .failed(let error):
                    if resumed.tryMark() { cont.resume(throwing: error) }
                case .cancelled:
                    if resumed.tryMark() { cont.resume(throwing: RpcError.connectionClosed) }
                default:
                    break
                }
            }
            conn.start(queue: DispatchQueue.global(qos: .userInitiated))
        }

        // 握手行（服务端校验 token 后回 ok/version/pid）
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            handshakeContinuation = cont
            startReceiving()
            sendLine(["v": 1, "token": token])
        }
    }

    public func close() {
        closed = true
        connection?.cancel()
        connection = nil
        failAllPending(RpcError.connectionClosed)
    }

    // -------- 请求 --------

    public func call(method: String, params: [String: Any] = [:]) async throws -> [String: Any] {
        guard let conn = connection, !closed else { throw RpcError.connectionClosed }
        let id = nextID
        nextID += 1
        let payload: [String: Any] = ["id": id, "method": method, "params": params]
        return try await withCheckedThrowingContinuation { cont in
            pending[id] = cont
            sendLine(payload, on: conn)
        }
    }

    // -------- 底层收发 --------

    private func sendLine(_ obj: [String: Any], on conn: NWConnection? = nil) {
        guard let data = try? JSONSerialization.data(withJSONObject: obj) else { return }
        var line = data
        line.append(UInt8(ascii: "\n"))
        (conn ?? connection)?.send(content: line, completion: .contentProcessed { _ in })
    }

    private func startReceiving() {
        connection?.receive(minimumIncompleteLength: 1, maximumLength: 65536) {
            [weak self] data, _, isComplete, error in
            guard let self else { return }
            Task { await self.onReceived(data: data, isComplete: isComplete, error: error) }
        }
    }

    private func onReceived(data: Data?, isComplete: Bool, error: NWError?) {
        if let data, !data.isEmpty {
            buffer.append(data)
            drainLines()
        }
        if error != nil || isComplete {
            closed = true
            failAllPending(RpcError.connectionClosed)
            disconnectHandler?()
            return
        }
        startReceiving()
    }

    private func drainLines() {
        while let range = buffer.range(of: Data([0x0A])) {
            let lineData = buffer.subdata(in: buffer.startIndex..<range.lowerBound)
            buffer.removeSubrange(buffer.startIndex...range.lowerBound)
            guard !lineData.isEmpty,
                  let obj = (try? JSONSerialization.jsonObject(with: lineData)) as? [String: Any]
            else { continue }
            handleLine(obj)
        }
    }

    private func handleLine(_ obj: [String: Any]) {
        // 握手应答优先（连接早期，无 id/event）
        if let cont = handshakeContinuation {
            handshakeContinuation = nil
            if obj["ok"] as? Bool == true {
                cont.resume()
            } else {
                cont.resume(throwing: RpcError.handshakeFailed(obj["error"] as? String ?? "未知错误"))
                closed = true
                connection?.cancel()
            }
            return
        }
        if let name = obj["event"] as? String {
            let data = obj["data"] as? [String: Any] ?? [:]
            eventHandler?(name, data)
            return
        }
        if let id = obj["id"] as? Int, let cont = pending.removeValue(forKey: id) {
            if obj["ok"] as? Bool == true {
                cont.resume(returning: obj["result"] as? [String: Any] ?? [:])
            } else {
                cont.resume(throwing: RpcError.requestFailed(
                    obj["error"] as? String ?? "未知错误",
                    obj["type"] as? String ?? "Error",
                    obj["data"] as? [String: Any] ?? [:]
                ))
            }
        }
    }

    private func failAllPending(_ error: Error) {
        for cont in pending.values { cont.resume(throwing: error) }
        pending.removeAll()
    }
}
