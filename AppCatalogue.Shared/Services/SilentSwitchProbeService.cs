using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AppCatalogue.Shared.Models;

namespace AppCatalogue.Shared.Services;

public sealed class SilentSwitchProbeService
{
    private static readonly string[] ProbeArguments = ["/?", "/help", "-help", "/h", "-h", "--help", "-?"];
    private readonly FileLogger _logger;

    public SilentSwitchProbeService(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<SilentSwitchProbeResult> ProbeAsync(
        string installerPath,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = installerPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return new SilentSwitchProbeResult
            {
                Summary = "Installer path is empty.",
                Confidence = "Low",
                CommandPreview = string.Empty
            };
        }

        if (!File.Exists(normalizedPath))
        {
            return new SilentSwitchProbeResult
            {
                Summary = "Installer file does not exist.",
                Confidence = "Low",
                CommandPreview = string.Empty
            };
        }

        var extension = Path.GetExtension(normalizedPath).ToLowerInvariant();
        if (extension == ".msi")
        {
            var msiSuggestion = "/qn /norestart";
            var commandPreview = $"msiexec /i \"{Path.GetFileName(normalizedPath)}\" {msiSuggestion}";
            _logger.Log($"Silent switch probe: MSI detected for '{normalizedPath}'. Suggested '{msiSuggestion}'.");

            return new SilentSwitchProbeResult
            {
                IsMsi = true,
                Summary = "MSI installer detected. Applied standard silent arguments.",
                Confidence = "High",
                CommandPreview = commandPreview,
                Suggestions = [msiSuggestion],
                RawOutput = "MSI package detected. Probing was skipped.",
                Attempts = []
            };
        }

        var attempts = new List<SilentSwitchProbeAttempt>();
        var outputBuilder = new StringBuilder();

        _logger.Log($"Silent switch probe started for '{normalizedPath}'.");

        foreach (var probeArgument in ProbeArguments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = await RunProbeAttemptAsync(normalizedPath, probeArgument, timeoutMs, cancellationToken);
            attempts.Add(attempt);

            outputBuilder.AppendLine($"=== Probe: {probeArgument} ===");
            outputBuilder.AppendLine($"Started: {attempt.Started}");
            outputBuilder.AppendLine($"TimedOut: {attempt.TimedOut}");
            outputBuilder.AppendLine($"ExitCode: {attempt.ExitCode?.ToString() ?? "n/a"}");

            if (!string.IsNullOrWhiteSpace(attempt.Output))
            {
                outputBuilder.AppendLine(attempt.Output);
            }
            else
            {
                outputBuilder.AppendLine("[No console output captured]");
            }

            outputBuilder.AppendLine();
        }

        var rawOutput = outputBuilder.ToString();
        var suggestions = ExtractSuggestions(rawOutput);
        var confidence = ScoreConfidence(suggestions);
        var summary = suggestions.Count == 0
            ? "No silent switch could be detected automatically. Please confirm manually."
            : $"Detected {suggestions.Count} candidate silent argument set(s).";

        _logger.Log(
            $"Silent switch probe finished for '{normalizedPath}'. Suggestions={suggestions.Count}, Confidence={confidence}.");

        return new SilentSwitchProbeResult
        {
            IsMsi = false,
            Summary = summary,
            Confidence = confidence,
            CommandPreview = suggestions.Count > 0
                ? $"\"{Path.GetFileName(normalizedPath)}\" {suggestions[0]}"
                : $"\"{Path.GetFileName(normalizedPath)}\" [manual arguments required]",
            Suggestions = suggestions,
            RawOutput = rawOutput,
            Attempts = attempts
        };
    }

    private async Task<SilentSwitchProbeAttempt> RunProbeAttemptAsync(
        string installerPath,
        string arguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                _logger.Log($"Silent switch probe failed to start '{installerPath}' with '{arguments}'.");
                return new SilentSwitchProbeAttempt
                {
                    Arguments = arguments,
                    Started = false,
                    TimedOut = false,
                    ExitCode = null,
                    Output = "Failed to start process."
                };
            }

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var waitForExitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(timeoutMs, cancellationToken);
            var completedTask = await Task.WhenAny(waitForExitTask, timeoutTask);

            var timedOut = completedTask == timeoutTask;
            if (timedOut)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill failures. We still return timeout status.
                }
            }

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            var combined = BuildCombinedOutput(stdOut, stdErr);

            _logger.Log(
                $"Silent switch probe '{installerPath}' args '{arguments}': started=true, timedOut={timedOut}, exitCode={(timedOut ? "n/a" : process.ExitCode)}.");

            return new SilentSwitchProbeAttempt
            {
                Arguments = arguments,
                Started = true,
                TimedOut = timedOut,
                ExitCode = timedOut ? null : process.ExitCode,
                Output = combined
            };
        }
        catch (Exception ex)
        {
            _logger.Log($"Silent switch probe error for '{installerPath}' args '{arguments}': {ex.Message}");
            return new SilentSwitchProbeAttempt
            {
                Arguments = arguments,
                Started = false,
                TimedOut = false,
                ExitCode = null,
                Output = $"Probe failed: {ex.Message}"
            };
        }
    }

    private static string BuildCombinedOutput(string standardOutput, string standardError)
    {
        var outputBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            outputBuilder.AppendLine("[STDOUT]");
            outputBuilder.AppendLine(standardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            outputBuilder.AppendLine("[STDERR]");
            outputBuilder.AppendLine(standardError.Trim());
        }

        return outputBuilder.ToString().Trim();
    }

    private static List<string> ExtractSuggestions(string output)
    {
        var suggestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        if (Regex.IsMatch(output, @"(?i)/VERYSILENT"))
        {
            suggestions.Add("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
        }

        if (Regex.IsMatch(output, @"(?i)\b/silent\b"))
        {
            suggestions.Add("/silent /norestart");
        }

        if (Regex.IsMatch(output, @"(?i)\b/quiet\b"))
        {
            suggestions.Add("/quiet /norestart");
        }

        if (Regex.IsMatch(output, @"(?i)\b/qn\b"))
        {
            suggestions.Add("/qn /norestart");
        }

        if (Regex.IsMatch(output, @"(?i)\bunattended\b"))
        {
            suggestions.Add("/silent /norestart");
        }

        if (Regex.IsMatch(output, @"(?i)(\s|^)/S(\s|$)"))
        {
            suggestions.Add("/S");
        }

        if (Regex.IsMatch(output, @"(?i)(\s|^)/s(\s|$)"))
        {
            suggestions.Add("/s");
        }

        if (Regex.IsMatch(output, @"(?i)\b/SUPPRESSMSGBOXES\b"))
        {
            suggestions.Add("/silent /SUPPRESSMSGBOXES /norestart");
        }

        if (Regex.IsMatch(output, @"(?i)\b/NORESTART\b"))
        {
            suggestions.Add("/silent /norestart");
        }

        return suggestions.ToList();
    }

    private static string ScoreConfidence(List<string> suggestions)
    {
        if (suggestions.Count == 0)
        {
            return "Low";
        }

        if (suggestions.Any(s => s.Contains("/qn", StringComparison.OrdinalIgnoreCase) ||
                                 s.Contains("/VERYSILENT", StringComparison.OrdinalIgnoreCase)))
        {
            return "High";
        }

        if (suggestions.Any(s => s.Contains("/silent", StringComparison.OrdinalIgnoreCase) ||
                                 s.Contains("/quiet", StringComparison.OrdinalIgnoreCase)))
        {
            return "Medium";
        }

        return "Low";
    }
}
