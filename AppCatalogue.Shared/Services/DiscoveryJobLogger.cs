using System.Text.Json;
using AppCatalogue.Shared.Models;

namespace AppCatalogue.Shared.Services;

public sealed class DiscoveryJobLogger
{
    private readonly object _syncRoot = new();
    private readonly FileLogger? _appLogger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private DiscoveryStatusContract _status;

    public DiscoveryJobLogger(string jobId, string jobDirectory, FileLogger? appLogger = null)
    {
        JobId = string.IsNullOrWhiteSpace(jobId)
            ? Guid.NewGuid().ToString("N")
            : jobId.Trim();
        JobDirectory = jobDirectory;
        _appLogger = appLogger;

        Directory.CreateDirectory(JobDirectory);
        HostLogPath = Path.Combine(JobDirectory, "host.log");
        StatusPath = Path.Combine(JobDirectory, "status.json");

        _status = new DiscoveryStatusContract
        {
            JobId = JobId,
            State = "Pending",
            Stage = "JobCreated",
            Message = "Discovery job initialized.",
            Success = null,
            Error = string.Empty,
            UpdatedUtc = DateTime.UtcNow.ToString("O"),
            ResultPath = string.Empty,
            LogPath = HostLogPath
        };

        WriteStatus_NoLock();
    }

    public string JobId { get; }

    public string JobDirectory { get; }

    public string HostLogPath { get; }

    public string StatusPath { get; }

    public DiscoveryStatusContract CurrentStatus
    {
        get
        {
            lock (_syncRoot)
            {
                return CloneStatus(_status);
            }
        }
    }

    public void LogInfo(string stage, string message)
    {
        WriteLogLine("INFO", stage, message);
    }

    public void LogWarning(string stage, string message)
    {
        WriteLogLine("WARN", stage, message);
    }

    public void LogError(string stage, string message, Exception? exception = null)
    {
        var details = exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}";
        WriteLogLine("ERROR", stage, details);
    }

    public void UpdateStatus(
        string state,
        string stage,
        string message,
        bool? success = null,
        string? error = null,
        string? resultPath = null,
        string? logPath = null)
    {
        lock (_syncRoot)
        {
            _status.State = string.IsNullOrWhiteSpace(state) ? _status.State : state.Trim();
            _status.Stage = string.IsNullOrWhiteSpace(stage) ? _status.Stage : stage.Trim();
            _status.Message = message ?? string.Empty;
            _status.Success = success;
            _status.Error = error ?? string.Empty;
            _status.UpdatedUtc = DateTime.UtcNow.ToString("O");
            _status.ResultPath = resultPath ?? _status.ResultPath;
            _status.LogPath = logPath ?? _status.LogPath;
            WriteStatus_NoLock();
        }
    }

    private void WriteLogLine(string severity, string stage, string message)
    {
        var safeStage = string.IsNullOrWhiteSpace(stage) ? "General" : stage.Trim();
        var safeMessage = message ?? string.Empty;
        var line = $"{DateTime.UtcNow:O} [{severity}] [{safeStage}] {safeMessage}";

        lock (_syncRoot)
        {
            File.AppendAllText(HostLogPath, line + Environment.NewLine);
        }

        _appLogger?.Log($"DiscoveryJob {JobId} [{severity}] [{safeStage}] {safeMessage}");
    }

    private void WriteStatus_NoLock()
    {
        var json = JsonSerializer.Serialize(_status, _jsonOptions);
        File.WriteAllText(StatusPath, json);
    }

    private static DiscoveryStatusContract CloneStatus(DiscoveryStatusContract status)
    {
        return new DiscoveryStatusContract
        {
            JobId = status.JobId,
            Success = status.Success,
            State = status.State,
            Stage = status.Stage,
            Message = status.Message,
            Error = status.Error,
            UpdatedUtc = status.UpdatedUtc,
            ResultPath = status.ResultPath,
            LogPath = status.LogPath
        };
    }
}
