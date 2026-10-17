namespace XTrader.Shell.Core;

/// <summary>
/// 应用目录布局（与 Python 侧 src/infrastructure/paths.py 保持一致的定义）。
///
/// 打包态下所有可写数据集中在用户主目录的一个根目录下，与程序安装位置无关：
///
///   %USERPROFILE%\.X-iTrader-Client\data\              业务数据（后端 workdir、SQLite、token）
///   %USERPROFILE%\.X-iTrader-Client\runtime\backend\   壳首启下载的后端运行时（预留）
///
/// runtime/ 与 data/ 平级分离：后端运行时整体的安装或替换只动 runtime/backend，
/// 不触碰业务数据。
/// </summary>
internal static class AppPaths
{
    /// <summary>数据根目录名（位于用户主目录下）</summary>
    internal const string AppDirName = ".X-iTrader-Client";

    /// <summary>数据根目录（frozen 后端的 workdir）</summary>
    internal static string DataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppDirName);

    /// <summary>运行时根目录</summary>
    internal static string RuntimeDir => Path.Combine(DataRoot, "runtime");

    /// <summary>后端运行时目录：PyInstaller onedir 内容（可执行文件 + _internal/）</summary>
    internal static string BackendDir => Path.Combine(RuntimeDir, "backend");

    /// <summary>后端可执行文件（runtime 布局），由壳 spawn</summary>
    internal static string BackendExecutable =>
        Path.Combine(BackendDir, BackendProcess.BackendExeName);

    /// <summary>backend.json 的路径（后端启动后写入，壳据此连接）</summary>
    internal static string BackendInfoFile => Path.Combine(DataRoot, "data", "backend.json");
}
