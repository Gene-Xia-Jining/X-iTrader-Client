import Foundation

/// 应用目录布局（与 Python 侧 src/infrastructure/paths.py 保持一致的定义）。
///
/// 打包态下所有可写数据集中在用户主目录的一个根目录下，与 .app 安装位置无关：
///
///   ~/.X-iTrader-Client/data/              业务数据（后端 workdir、SQLite、token）
///
/// 后端不再是独立下载的运行时，而是 .app 的一部分（Contents/Resources/backend/），
/// 由 build.sh 用嵌入式 Python 拼装（见 specs/build_backend.py）。
public enum AppPaths {
    /// 数据根目录名（位于用户主目录下）
    public static let appDirName = ".X-iTrader-Client"

    /// 数据根目录（打包态后端的 workdir）
    public static var dataRoot: URL {
        URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(appDirName)
    }

    /// 后端日志文件（由后端自己写入，5MB × 6 份轮转）
    public static var backendLogFile: URL {
        dataRoot.appendingPathComponent("data/logs/backend.log")
    }
}
