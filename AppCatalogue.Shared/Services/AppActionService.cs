using System.Diagnostics;
using System.IO;
using System.Text;
using AppCatalogue.Shared.Models;

namespace AppCatalogue.Shared.Services;

public sealed class AppActionService
{
    private readonly FileLogger _logger;

    public AppActionService(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<AppActionResult> ExecuteAsync(AppEntry app, CancellationToken cancellationToken = default)
    {
        try
        {
            AppPaths.EnsureEndpointStructure();

            if (app.RequiresAdmin && !SecurityHelper.IsAdministrator())
            {
                if (TryCreateRequestMarker(app, out var requestFilePath, out var requestError))
                {
                    _logger.Log($"{app.Name}: administrator rights missing. Created request marker at {requestFilePath}.");
                    return AppActionResult.Ok("Requested", "Administrator rights required. Request has been logged.");
                }

                var message = $"Administrator rights are required for this action. {requestError}";
                _logger.Log($"{app.Name}: {message}");
                return AppActionResult.Fail(message);
            }

            return app.InstallerSourceType switch
            {
                InstallerSourceType.FileServer => await ExecuteFileServerInstallAsync(app, cancellationToken),
                InstallerSourceType.Winget => await ExecuteWingetInstallAsync(app, cancellationToken),
                _ => AppActionResult.Fail("Unsupported install type.")
            };
        }
        catch (OperationCanceledException)
        {
            _logger.Log($"{app.Name}: action canceled.");
            return AppActionResult.Fail("Action canceled by user.");
        }
        catch (Exception ex)
        {
            _logger.Log($"{app.Name}: execution failed - {ex.Message}");
            return AppActionResult.Fail(ex.Message);
        }
    }

    private async Task<AppActionResult> ExecuteFileServerInstallAsync(AppEntry app, CancellationToken cancellationToken)
    {
        var installerPath = Environment.ExpandEnvironmentVariables(app.InstallerPath.Trim());
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            const string message = "Installer path is missing.";
            _logger.Log($"{app.Name}: {message}");
            return AppActionResult.Fail(message, "Installer Missing");
        }

        if (!File.Exists(installerPath))
        {
            var message = $"Installer not found at '{installerPath}'.";
            _logger.Log($"{app.Name}: {message}");
            return AppActionResult.Fail(message, "Installer Missing");
        }

        var extension = Path.GetExtension(installerPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".exe";
        }

        var cachedInstallerPath = Path.Combine(
            AppPaths.EndpointCacheDirectory,
            $"{AppPaths.SanitizePathSegment(app.Name, "installer")}_{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}");

        try
        {
            _logger.Log($"{app.Name}: copying installer from '{installerPath}' to cache '{cachedInstallerPath}'.");
            await Task.Run(() => File.Copy(installerPath, cachedInstallerPath, overwrite: true), cancellationToken);

            var startInfo = BuildInstallerStartInfo(cachedInstallerPath, app.SilentArguments);
            _logger.Log($"{app.Name}: starting installer '{startInfo.FileName}' {startInfo.Arguments}");

            var execution = await RunProcessAsync(startInfo, cancellationToken);
            _logger.Log($"{app.Name}: installer exited with code {execution.ExitCode}.");
            LogProcessStreams(app.Name, execution);

            if (execution.ExitCode != 0)
            {
                return AppActionResult.Fail(
                    $"Installer exited with code {execution.ExitCode}. Check logs for details.",
                    "Failed");
            }

            return AppActionResult.Ok("Ready", "Installer completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.Log($"{app.Name}: file-server install failed - {ex.Message}");
            return AppActionResult.Fail(ex.Message);
        }
        finally
        {
            CleanupCachedInstaller(cachedInstallerPath, app.Name);
        }
    }

    private async Task<AppActionResult> ExecuteWingetInstallAsync(AppEntry app, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.WingetId))
        {
            return AppActionResult.Fail("WingetId is missing for this app.");
        }

        var wingetArguments = string.IsNullOrWhiteSpace(app.WingetArguments)
            ? AppConfigService.DefaultWingetArguments
            : app.WingetArguments.Trim();

        var startInfo = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = $"install --id \"{app.WingetId}\" -e {wingetArguments}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _logger.Log($"{app.Name}: running winget install for id '{app.WingetId}'.");
        var execution = await RunProcessAsync(startInfo, cancellationToken);
        _logger.Log($"{app.Name}: winget exited with code {execution.ExitCode}.");
        LogProcessStreams(app.Name, execution);

        if (execution.ExitCode != 0)
        {
            return AppActionResult.Fail($"winget exited with code {execution.ExitCode}.", "Failed");
        }

        return AppActionResult.Ok("Ready", "winget installation command completed.");
    }

    private static ProcessStartInfo BuildInstallerStartInfo(string localInstallerPath, string silentArguments)
    {
        var extension = Path.GetExtension(localInstallerPath).ToLowerInvariant();
        if (extension == ".msi")
        {
            var msiArguments = string.IsNullOrWhiteSpace(silentArguments)
                ? "/qn /norestart"
                : silentArguments.Trim();

            return new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{localInstallerPath}\" {msiArguments}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = localInstallerPath,
            Arguments = string.IsNullOrWhiteSpace(silentArguments) ? string.Empty : silentArguments.Trim(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private static async Task<ProcessExecutionResult> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start process.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        return new ProcessExecutionResult(process.ExitCode, standardOutput, standardError);
    }

    private bool TryCreateRequestMarker(AppEntry app, out string requestPath, out string error)
    {
        requestPath = string.Empty;
        error = string.Empty;

        try
        {
            var markerFileName =
                $"{DateTime.Now:yyyyMMdd_HHmmss}_{AppPaths.SanitizePathSegment(app.Name, "RequestedApp")}.request.txt";
            requestPath = Path.Combine(AppPaths.EndpointRequestsDirectory, markerFileName);

            var content =
                $"AppName={app.Name}{Environment.NewLine}" +
                $"RequestedBy={Environment.UserName}{Environment.NewLine}" +
                $"Machine={Environment.MachineName}{Environment.NewLine}" +
                $"Timestamp={DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                "Reason=Requires administrator rights";

            File.WriteAllText(requestPath, content, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void CleanupCachedInstaller(string cachedInstallerPath, string appName)
    {
        if (!File.Exists(cachedInstallerPath))
        {
            return;
        }

        try
        {
            File.Delete(cachedInstallerPath);
            _logger.Log($"{appName}: cache cleanup removed '{cachedInstallerPath}'.");
        }
        catch (Exception ex)
        {
            _logger.Log($"{appName}: cache cleanup failed for '{cachedInstallerPath}': {ex.Message}");
        }
    }

    private void LogProcessStreams(string appName, ProcessExecutionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.Log($"{appName}: stdout: {TrimForLog(result.StandardOutput)}");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            _logger.Log($"{appName}: stderr: {TrimForLog(result.StandardError)}");
        }
    }

    private static string TrimForLog(string text)
    {
        var trimmed = text.Replace(Environment.NewLine, " ").Trim();
        return trimmed.Length <= 800 ? trimmed : trimmed[..800];
    }

    private sealed record ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError);
}
