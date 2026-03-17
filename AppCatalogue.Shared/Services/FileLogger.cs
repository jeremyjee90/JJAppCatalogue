using System.IO;

namespace AppCatalogue.Shared.Services;

public sealed class FileLogger
{
    private readonly object _syncRoot = new();
    private readonly string _logPath;

    public FileLogger(string logPath)
    {
        _logPath = ResolveWritableLogPath(logPath);
    }

    public event Action<string>? MessageLogged;

    public string LogPath => _logPath;

    public void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (_syncRoot)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }

        MessageLogged?.Invoke(line);
    }

    private static string ResolveWritableLogPath(string requestedPath)
    {
        if (TryEnsureLogDirectory(requestedPath))
        {
            return requestedPath;
        }

        var fileName = Path.GetFileName(requestedPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "AppCatalogue.log";
        }

        var programDataFallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AppCatalogue",
            "Logs",
            fileName);

        if (TryEnsureLogDirectory(programDataFallback))
        {
            return programDataFallback;
        }

        var tempFallback = Path.Combine(Path.GetTempPath(), fileName);
        if (TryEnsureLogDirectory(tempFallback))
        {
            return tempFallback;
        }

        return requestedPath;
    }

    private static bool TryEnsureLogDirectory(string logPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
