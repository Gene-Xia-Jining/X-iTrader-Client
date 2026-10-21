import Foundation

/// 应用目录布局（与 Python 侧 src/infrastructure/paths.py 保持一致的定义）。
///
/// 打包态下所有可写数据集中在用户主目录的一个根目录下，与 .app 安装位置无关：
///
///   ~/.X-iTrader-Client/data/              业务数据（后端 workdir、SQLite、token）
///   ~/.X-iTrader-Client/runtime/backend/   壳首启下载的后端运行时
///
/// runtime/ 与 data/ 平级分离：后端运行时整体的安装或替换只动 runtime/backend，
/// 不触碰业务数据。
public enum AppPaths {
    /// 数据根目录名（位于用户主目录下）
    public static let appDirName = ".X-iTrader-Client"

    /// 数据根目录（frozen 后端的 workdir）
    public static var dataRoot: URL {
        URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(appDirName)
    }

    /// 运行时根目录
    public static var runtimeDir: URL {
        dataRoot.appendingPathComponent("runtime")
    }

    /// 后端运行时目录：PyInstaller onedir 内容（backend 可执行文件 + _internal/）
    public static var backendDir: URL {
        runtimeDir.appendingPathComponent("backend")
    }

    /// 后端可执行文件（runtime 布局），由壳 spawn
    public static var backendExecutable: URL {
        backendDir.appendingPathComponent("backend")
    }

    /// 后端日志文件（由后端自己写入，5MB × 6 份轮转）
    public static var backendLogFile: URL {
        dataRoot.appendingPathComponent("data/logs/backend.log")
    }
}
