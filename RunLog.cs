using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

internal static class RunLog
{
    private static readonly StringBuilder Entries = new StringBuilder();
    private static string? filePath;

    internal static void Info(string message) => Write("INFO", message);

    internal static void Error(Exception exception) => Write("ERROR", exception.ToString());

    internal static string SaveFailure()
    {
        var name = "PCL-Download-Enable-" +
                   DateTimeOffset.Now.ToString("yyyyMMddTHHmmssfffzzz", CultureInfo.InvariantCulture).Replace(":", "") +
                   "-" + Process.GetCurrentProcess().Id + ".log";
        foreach (var directory in new[] { AppDomain.CurrentDomain.BaseDirectory, Path.GetTempPath() })
        {
            var path = filePath ?? Path.Combine(directory, name);
            try
            {
                var savedLine = Format("INFO", "错误日志已写入：" + path);
                File.WriteAllText(path, Entries.ToString() + savedLine + Environment.NewLine,
                    new UTF8Encoding(false));
                filePath = path;
                Entries.AppendLine(savedLine);
                Console.WriteLine(savedLine);
                return path;
            }
            catch (Exception ex)
            {
                Write("WARN", "无法写入日志到 " + directory + "：" + ex.Message);
            }
        }
        return "日志写入失败，请查看控制台输出";
    }

    private static void Write(string level, string message)
    {
        var line = Format(level, message);
        Entries.AppendLine(line);
        if (level == "ERROR") Console.Error.WriteLine(line);
        else Console.WriteLine(line);
    }

    private static string Format(string level, string message) =>
        $"[{DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)}] [{level}] {message}";
}
