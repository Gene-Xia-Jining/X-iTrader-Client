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
/// - 生产态（runtime）：壳首启下载的解压产物位于 ~/.X-iTrader-Client/runtime/backend/
///   （见 AppPaths），工作目录为数据根 ~/.X-iTrader-Client（frozen 后端自行 chdir
///   到同一路径，data/ 与 app 位置无关）；这是重构后的唯一生产布局。
/// - 生产态（bundle，过渡兼容）：早期构建把 PyInstaller 后端打进 Bundle 的
///   Resources/backend/；该回退在打包流程改造完成后可移除。
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
                return "未找到打包后端可执行文件: \(path)"
            case .infoTimeout(let path):
                return "等待 \(path) 超时，后端可能启动失败（详见 \(AppPaths.backendLogFile.path)）"
            case .alreadyRunning:
                return "后端已在运行"
            }
        }
    }

    /// 启动方式：frozen = bundle 内 PyInstaller 后端；python = .venv 解释器（开发态）
    private enum LaunchPlan {
        case frozen(executable: URL, workdir: URL)
        case python(repoRoot: URL)

        var workdir: URL {
            switch self {
            case .frozen(_, let workdir): return workdir
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
    /// （python 模式 = 业务根，frozen 模式 = 数据根 ~/.X-iTrader-Client）
    public init(repoRootOverride: URL? = nil, workdirOverride: URL? = nil) throws {
        self.workdirOverride = workdirOverride
        if let override = repoRootOverride {
            self.plan = .python(repoRoot: override)
        } else {
            self.plan = try Self.detectPlan()
        }
        self.repoRoot = plan.workdir
        switch plan {
        case .frozen(let executable, _):
            guard FileManager.default.fileExists(atPath: executable.path) else {
                throw BackendError.backendMissing(executable.path)
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
        case .frozen(let executable, _):
            proc.executableURL = executable
            proc.arguments = []
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
        // 生产态（runtime）：壳首启下载并解压到数据根下的 runtime/backend/，
        // workdir 为数据根（frozen 后端 chdir 到同一路径）
        let runtimeBackend = AppPaths.backendExecutable
        if FileManager.default.fileExists(atPath: runtimeBackend.path) {
            return .frozen(executable: runtimeBackend, workdir: AppPaths.dataRoot)
        }
        // 生产态（bundle，过渡兼容）：早期构建把 PyInstaller 后端打进
        // Resources/backend/（放 MacOS 会被 codesign 当代码树扫描而无法通过签名）
        if let backend = Bundle.main.url(
            forResource: "backend", withExtension: nil, subdirectory: "backend"
        ), FileManager.default.fileExists(atPath: backend.path) {
            return .frozen(executable: backend, workdir: AppPaths.dataRoot)
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
}
