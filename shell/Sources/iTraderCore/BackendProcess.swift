import Foundation

/// data/backend.json 的内容：后端启动后写入，壳据此连接。
public struct BackendInfo: Codable {
    public let port: Int
    public let token: String
    public let pid: Int
    public let version: String
}

/// 后端进程管理：定位启动命令、spawn、等待发现文件、健康检查、终止。
///
/// 布局探测（按序取其一）：
/// - 生产态：.app 内的 Contents/Resources/backend/（内含 runtime/ 与 code/），
///   由 build.sh 拼装。启动命令固定为
///   `runtime/bin/python3.X -E -s code/backend_entry.py`，workdir 为数据根
///   ~/.X-iTrader-Client（后端自行 chdir 到同一路径，data/ 与 app 位置无关）。
/// - 源码开发态：从本文件编译位置向上查找 src/backend/__main__.py 所在仓库根，
///   为 python 模式：.venv/bin/python -m src.backend，业务根须含 .venv 与 src/。
public final class BackendProcess {
    public enum BackendError: LocalizedError, Sendable {
        case layoutNotFound
        case pythonMissing(String)
        case backendMissing(String)
        case infoTimeout(String)
        case alreadyRunning

        public var errorDescription: String? {
            switch self {
            case .layoutNotFound:
                return "未找到后端业务根目录（需要 .venv 与 src/backend）"
            case .pythonMissing(let path):
                return "未找到 Python 解释器: \(path)"
            case .backendMissing(let path):
                return "未找到打包后端: \(path)"
            case .infoTimeout(let path):
                return "等待 \(path) 超时，后端可能启动失败（详见 \(AppPaths.backendLogFile.path)）"
            case .alreadyRunning:
                return "后端已在运行"
            }
        }
    }

    /// 启动方式：packaged = .app 内嵌入式运行时；python = .venv 解释器（开发态）
    private enum LaunchPlan {
        case packaged(python: URL, entry: URL, workdir: URL)
        case python(repoRoot: URL)

        var workdir: URL {
            switch self {
            case .packaged(_, _, let workdir): return workdir
            case .python(let repoRoot): return repoRoot
            }
        }
    }

    let repoRoot: URL
    private let plan: LaunchPlan
    private let workdirOverride: URL?
    private var process: Process?
    private(set) var info: BackendInfo?

    public var workdir: URL { workdirOverride ?? plan.workdir }
    public var infoURL: URL { workdir.appendingPathComponent("data/backend.json") }

    /// 崩溃/退出回调（主线程外触发）
    public var onExit: (() -> Void)?

    /// workdirOverride：测试用临时目录隔离 data/；生产环境用默认
    /// （python 模式 = 业务根，packaged 模式 = 数据根 ~/.X-iTrader-Client）
    public init(repoRootOverride: URL? = nil, workdirOverride: URL? = nil) throws {
        self.workdirOverride = workdirOverride
        if let override = repoRootOverride {
            self.plan = .python(repoRoot: override)
        } else {
            self.plan = try Self.detectPlan()
        }
        self.repoRoot = plan.workdir
        switch plan {
        case .packaged(let python, let entry, _):
            guard FileManager.default.fileExists(atPath: python.path) else {
                throw BackendError.pythonMissing(python.path)
            }
            guard FileManager.default.fileExists(atPath: entry.path) else {
                throw BackendError.backendMissing(entry.path)
            }
        case .python(let root):
            let python = Self.pythonURL(repoRoot: root)
            guard FileManager.default.fileExists(atPath: python.path) else {
                throw BackendError.pythonMissing(python.path)
            }
        }
    }

    /// 已有后端在运行（backend.json 中 pid 存活）时直接复用，不 spawn 新进程。
    public func connectOrStart() async throws -> BackendInfo {
        if let existing = Self.readInfo(from: infoURL), Self.isPidAlive(existing.pid) {
            info = existing
            return existing
        }
        return try await start()
    }

    public func start() async throws -> BackendInfo {
        guard process == nil else { throw BackendError.alreadyRunning }
        try? FileManager.default.removeItem(at: infoURL)

        let proc = Process()
        switch plan {
        case .packaged(let python, let entry, _):
            proc.executableURL = python
            // -E -s：隔离环境变量与 user site，保证只认包内 site-packages
            proc.arguments = ["-E", "-s", entry.path]
            proc.currentDirectoryURL = workdir
        case .python(let root):
            proc.executableURL = Self.pythonURL(repoRoot: root)
            proc.arguments = ["-m", "src.backend"]
            proc.currentDirectoryURL = workdir
            var env = ProcessInfo.processInfo.environment
            env["PYTHONPATH"] = root.path
            proc.environment = env
        }
        // 后端日志默认丢弃（发现文件是主通道）；调 ITRADER_BACKEND_VERBOSE=1 时继承输出便于排障
        if ProcessInfo.processInfo.environment["ITRADER_BACKEND_VERBOSE"] == nil {
            proc.standardOutput = FileHandle.nullDevice
            proc.standardError = FileHandle.nullDevice
        }
        proc.terminationHandler = { [weak self] _ in
            self?.process = nil
            self?.onExit?()
        }
        try proc.run()
        process = proc

        guard let found = await waitForInfo(timeout: 15) else {
            throw BackendError.infoTimeout(infoURL.path)
        }
        info = found
        return found
    }

    public func terminate() {
        guard let proc = process, proc.isRunning else { return }
        proc.interrupt()  // SIGINT：后端按优雅退出路径释放 TqApi 连接
        let deadline = Date().addingTimeInterval(3)
        while proc.isRunning && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.05)
        }
        if proc.isRunning { proc.terminate() }
    }

    // -------- helpers --------

    private func waitForInfo(timeout: TimeInterval) async -> BackendInfo? {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let info = Self.readInfo(from: infoURL) { return info }
            try? await Task.sleep(nanoseconds: 200_000_000)
        }
        return nil
    }

    public static func readInfo(from url: URL) -> BackendInfo? {
        guard let data = try? Data(contentsOf: url),
              let info = try? JSONDecoder().decode(BackendInfo.self, from: data)
        else { return nil }
        return info
    }

    public static func isPidAlive(_ pid: Int) -> Bool {
        kill(pid_t(pid), 0) == 0
    }

    private static func pythonURL(repoRoot: URL) -> URL {
        if let override = ProcessInfo.processInfo.environment["ITRADER_PYTHON"] {
            return URL(fileURLWithPath: override)
        }
        return repoRoot.appendingPathComponent(".venv/bin/python")
    }

    private static func detectPlan() throws -> LaunchPlan {
        // 生产态：.app 内 Resources/backend/（runtime/ + code/），由 build.sh 拼装
        if let backend = Bundle.main.url(
            forResource: "backend", withExtension: nil, subdirectory: nil
        ), let plan = packagedPlan(backendRoot: backend) {
            return plan
        }
        // 源码开发态：从源码位置向上找
        var dir = URL(fileURLWithPath: (#filePath as NSString).deletingLastPathComponent)
        for _ in 0..<8 {
            if FileManager.default.fileExists(atPath: dir.appendingPathComponent("src/backend/__main__.py").path) {
                return .python(repoRoot: dir)
            }
            dir.deleteLastPathComponent()
        }
        throw BackendError.layoutNotFound
    }

    /// 在 backend 根目录下探测解释器与入口脚本。
    ///
    /// 解释器名带版本号（python3.14），所以扫 bin/ 而不是硬编码；这样升级
    /// 运行时版本不用改壳代码。
    private static func packagedPlan(backendRoot: URL) -> LaunchPlan? {
        let fm = FileManager.default
        let entry = backendRoot.appendingPathComponent("code/backend_entry.py")
        guard fm.fileExists(atPath: entry.path) else { return nil }

        let binDir = backendRoot.appendingPathComponent("runtime/bin")
        guard let names = try? fm.contentsOfDirectory(atPath: binDir.path) else { return nil }
        // 只看 python3 / python3.X 本体：*-config 是编译扩展用的脚本，不是解释器
        let candidates = names
            .filter { $0.hasPrefix("python3") && !$0.hasSuffix("-config") }
            .sorted()
        guard let name = candidates.last else { return nil }

        let python = binDir.appendingPathComponent(name)
        guard fm.isExecutableFile(atPath: python.path) else { return nil }
        return .packaged(python: python, entry: entry, workdir: AppPaths.dataRoot)
    }
}
