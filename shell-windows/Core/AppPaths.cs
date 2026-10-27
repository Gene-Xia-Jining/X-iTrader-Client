namespace XTrader.Shell.Core;

/// <summary>
/// 应用目录布局（与 Python 侧 src/infrastructure/paths.py 保持一致的定义）。
///
/// 打包态下所有可写数据集中在用户主目录的一个根目录下，与程序安装位置无关：
///
///   %USERPROFILE%\.X-iTrader-Client\data\   业务数据（后端 workdir、SQLite、token）
///
/// 后端是程序目录的一部分（backend\runtime\ + backend\code\），由 CI 用嵌入式
/// Python 拼装（见 specs/build_backend.py），不再单独下载。
/// </summary>
internal static class AppPaths
{
    /// <summary>数据根目录名（位于用户主目录下）</summary>
    internal const string AppDirName = ".X-iTrader-Client";

    /// <summary>数据根目录（打包态后端的 workdir）</summary>
    internal static string DataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppDirName);

    /// <summary>backend.json 的路径（后端启动后写入，壳据此连接）</summary>
    internal static string BackendInfoFile => Path.Combine(DataRoot, "data", "backend.json");

    /// <summary>后端日志文件（由后端自己写入，5MB × 6 份轮转）</summary>
    internal static string BackendLogFile => Path.Combine(DataRoot, "data", "logs", "backend.log");
}
