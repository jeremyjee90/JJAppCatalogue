namespace AppCatalogue.Shared.Models;

public sealed class DiscoveryModeSettings
{
    public string VmName { get; set; } = "AppCatalogueLab01";
    public string CheckpointName { get; set; } = "CleanState";
    public string GuestInputDirectory { get; set; } = @"C:\Discovery\Input";
    public string GuestOutputDirectory { get; set; } = @"C:\Discovery\Output";
    public string GuestScriptsDirectory { get; set; } = @"C:\Discovery\Scripts";
    public string HostStagingDirectory { get; set; } = @"C:\Installers\Discovery\HostStaging";
    public string HostResultsDirectory { get; set; } = @"C:\Installers\Discovery\Results";
    public int GuestReadyTimeoutSeconds { get; set; } = 300;
    public int DiscoveryTimeoutSeconds { get; set; } = 1800;
    public int CommandTimeoutSeconds { get; set; } = 120;
    public int ProbeTimeoutSeconds { get; set; } = 15;
    public int InstallerTimeoutSeconds { get; set; } = 1200;
    public bool ShutdownVmOnComplete { get; set; } = true;
}
