using System;
using System.IO;
using System.Text;

namespace ClassSlideSync.ToastHelper
{
    /// <summary>
    /// 这个工具的日志。它是 WinExe（没有控制台窗口，不能在教室里闪一下黑框），
    /// 所以失败原因只能写文件。
    ///
    /// 只有调用方显式给 --log 才写：helper 一般躺在插件的安装目录里，那个目录不保证可写，
    /// 而且用户态工具不该在用户磁盘上到处留文件。插件每次调用都会把 --log 指到自己的
    /// PluginConfigFolder（ClassIsland 保证那里可写），所以真实环境里日志永远是有的。
    ///
    /// 日志本身**绝不抛异常**：写日志失败不能把「弹通知」这件事带崩。
    /// </summary>
    internal sealed class Log
    {
        readonly string? path;
        readonly object gate = new object();

        internal Log(string? path)
        {
            this.path = string.IsNullOrWhiteSpace(path) ? null : path;
        }

        internal string? Path => path;

        internal void Write(string message)
        {
            if (path == null) return;
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                              " [" + Environment.ProcessId + "] " + message + Environment.NewLine;
                lock (gate)
                {
                    string? dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // 用户没有写权限 / 磁盘满了：静默。通知能不能弹不取决于日志。
            }
        }
    }
}
