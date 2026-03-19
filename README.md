# AppCatalogue MSP Suite (.NET 8 WPF)

This solution contains two desktop apps in one solution:

1. `AppCatalogue` (end-user, read-only)
2. `AppCatalogueAdmin` (admin/support packaging console)

## Test Mode Pathing (Current)

For your current testing setup, this machine acts as the file server root:

- `C:\Installers`

Admin-side folders used by the app:

- `C:\Installers\Config\apps.json`
- `C:\Installers\Repository\`
- `C:\Installers\Logs\AppCatalogueAdmin.log`
- `C:\Installers\Scripts\`
- `C:\Installers\Admin\`
- `C:\Installers\Discovery\HostStaging\`
- `C:\Installers\Discovery\Results\`
- `C:\Installers\Discovery\Scripts\`

Endpoint-side folders used by the end-user app:

- `C:\ProgramData\AppCatalogue\Config\apps.json`
- `C:\ProgramData\AppCatalogue\Cache\`
- `C:\ProgramData\AppCatalogue\Logs\AppCatalogue.log`
- `C:\ProgramData\AppCatalogue\Icons\`
- `C:\ProgramData\AppCatalogue\Requests\`

## Solution Structure

```text
AppCatalogueSuite.sln
AppCatalogue/                 End-user WPF app
AppCatalogueAdmin/            Admin WPF app
AppCatalogue.Shared/          Shared models/services
SampleData/
  AppCatalogue/
    Config/apps.json          Sample schema
    Scripts/*.bat             Optional sample helper scripts
publish_all.bat               Publish both apps
run_appcatalogue.bat          Launch published AppCatalogue.exe
run_appcatalogueadmin.bat     Launch published AppCatalogueAdmin.exe
```

## Config Schema

`apps.json` shape:

```json
{
  "ConfigVersion": "1.0.0",
  "Apps": [
    {
      "Name": "Google Chrome",
      "Description": "Web browser",
      "Category": "Browsers",
      "Enabled": true,
      "InstallerSourceType": "FileServer",
      "InstallerPath": "C:\\Installers\\Repository\\Google Chrome\\installer.exe",
      "SilentArguments": "/silent /install",
      "WingetId": "",
      "WingetArguments": "--silent --accept-package-agreements --accept-source-agreements",
      "PrimaryDetection": {
        "Type": "RegistryDisplayName",
        "Value": "Google Chrome"
      },
      "SecondaryDetection": {
        "Type": "FileExists",
        "Value": "%ProgramFiles%\\Google\\Chrome\\Application\\chrome.exe"
      },
      "IconPath": "C:\\Installers\\Repository\\Google Chrome\\chrome.png",
      "RequiresAdmin": true
    }
  ]
}
```

Supported installer source types:

- `FileServer`
- `Winget`

Supported detection types:

- `RegistryDisplayName`
- `RegistryKeyExists`
- `FileExists`

## AppCatalogue (End-user)

Behavior:

- Reads local config: `C:\ProgramData\AppCatalogue\Config\apps.json`
- Displays enabled apps as install cards
- Read-only UI (no editing)
- Supports statuses: `Ready`, `Installing`, `Installed`, `Installer Missing`, `Requested`, `Failed`

Install behavior:

- `FileServer`: copy installer to local cache -> run locally -> wait -> cleanup cached file
- `Winget`: run winget command with configured id/arguments and wait for completion
- If app requires admin and user is not elevated, a request marker is created

## AppCatalogueAdmin (Admin)

Behavior:

- Loads/saves config at `C:\Installers\Config\apps.json`
- DataGrid + edit form UI for app management
- Supports add/edit/delete/duplicate/validate/save/reload
- Drag/drop or browse installer import (`.exe`, `.msi`)
- Copies imported installers into `C:\Installers\Repository\{AppName}\installer.ext`
- Silent switch helper probes common help switches and suggests likely silent arguments
- Detection rule helper suggests `RegistryDisplayName`, `FileExists`, and `RegistryKeyExists` rules with confidence levels
- Supports multi-rule detection per app:
  - required `PrimaryDetection`
  - optional `SecondaryDetection` fallback
- Includes `Explain Detection Rule`, `Test Detection`, and `One-Click Discovery` actions
- Per discovery run, creates a host job folder with detailed logs/artifacts:
  - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\host.log`
  - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\status.json`
  - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\guest-artifacts\...`
- Manual override is always available

## Silent Switch Helper Notes

- MSI auto suggestion: `/qn /norestart`
- EXE probing is best effort only
- Some installers provide no console help output
- Always verify arguments before production rollout

## Detection Suggestion Helper Notes

- Trigger manually using `Suggest Detection Rule`
- Also refreshes after installer import and silent-switch probe completion
- Uses app name, installer filename, installer file metadata (when available), probe output hints, and built-in heuristics
- Includes practical heuristics for:
  - Google Chrome, Mozilla Firefox, Discord, 7-Zip, Notepad++, VLC, Adobe Acrobat Reader, Zoom, TeamViewer, AnyDesk
- Suggestions are helpers only and are not auto-applied to detection fields without user action
- You can assign a suggestion as Primary or Secondary detection from the UI

## Detection Test and Discovery

- `Test Detection` runs current primary/secondary detection rules on the current machine and reports PASS/FAIL details
- `One-Click Discovery` uses Hyper-V checkpointed packaging flow (no guest password prompts)
- Discovery suggestions are always best-effort and must be reviewed before applying/saving

## Discovery Logging and Artifacts

Each discovery run is traceable end-to-end with per-job artifacts:

- Host job folder:
  - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\host.log`
  - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\status.json`
- Collected guest artifacts (copied back by host, even on failure when possible):
  - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\guest-artifacts\Jobs\<JobId>\guest.log`
  - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\guest-artifacts\Jobs\<JobId>\discovery-status.json`
  - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\guest-artifacts\Jobs\<JobId>\discovery-results.json`
- Legacy copy for compatibility:
  - `C:\Installers\Discovery\Results\<JobId>\...`

Host status stages include:

- `JobCreated`
- `CheckpointRestoreStart` / `CheckpointRestoreComplete`
- `InstallerCopyStart` / `InstallerCopyComplete`
- `GuestBootstrapStart` / `GuestBootstrapComplete`
- `WaitForGuestSignalStart`
- `GuestSignalReceived`
- `ResultCollectionStart` / `ResultCollectionComplete`
- `ResultParseStart` / `ResultParseComplete`
- `DiscoveryCompleted` / `DiscoveryFailed` / `Timeout`

Guest stages include:

- `WatcherStarted`
- `JobDetected`
- `JobLoaded`
- `InstallerFound`
- `DiscoveryStarted`
- `SilentSwitchAnalysisStarted`
- `SilentSwitchAttempt`
- `SilentSwitchAnalysisComplete`
- `DetectionRuleAnalysisStarted`
- `DetectionRuleAnalysisComplete`
- `ResultWriteStart` / `ResultWriteComplete`
- `CompletionSignalStart` / `CompletionSignalComplete`
- `DiscoverySucceeded` / `DiscoveryFailed`

Use the new AppCatalogueAdmin buttons:

- `Open Job Folder`
- `Open Logs Folder`

to jump directly to troubleshooting artifacts.

## Silent Switch Retry Order and Confidence

Discovery now uses a ranked attempt pipeline:

1. MSI: `msiexec /i "<installer>" /qn /norestart` first.
2. EXE with clear help output: help-derived switches first.
3. Fingerprint-based fallbacks next (Inno Setup, NSIS, InstallShield, WiX/Burn).
4. Generic fallback switches last.

Each attempt records:

- attempt number
- selected arguments and why
- installer fingerprint family
- process start/timed out/completed
- exit code
- artifact evidence deltas (uninstall entries, files, services, shortcuts, processes)
- pass/fail assessment

Recommendations include:

- `SilentRecommendation` (command/arguments)
- confidence label (`High`, `Medium`, `Low`)
- confidence score
- manual-review-needed indicator
- full ordered attempt history

## Hyper-V Discovery Mode Setup

Use these exact names:

- VM Name: `AppCatalogueLab01`
- Checkpoint Name: `CleanState`
- Guest Username: `DiscoveryAdmin`

### 1) Hyper-V prerequisites

- Hyper-V feature enabled on host
- Host PowerShell running as local administrator
- Guest Services integration enabled (the app also attempts to enable this)
- VM boots and can reach desktop

### 2) Name the VM and checkpoint exactly

- VM must be named `AppCatalogueLab01`
- Checkpoint must be named `CleanState`
- If you use different names, set them in the Admin UI fields before running discovery

### 3) One-time guest bootstrap (no-password model)

The system is designed to avoid guest credential prompts during normal discovery runs.
One-time setup is still needed inside the VM once:

1. On host, run:
   - `Prepare-DiscoveryHost.ps1`
   - `Prepare-DiscoveryVmGuest.ps1`
2. Open VM console, sign in as `DiscoveryAdmin`
3. Run elevated PowerShell in guest:
   - `C:\Discovery\Scripts\Install-DiscoveryBootstrap.ps1`
4. Verify scheduled task `AppCatalogueDiscoveryWatcher` is installed/running
5. Create/update checkpoint `CleanState` after bootstrap is installed

### 4) What One-Click Discovery automates

When tech clicks `One-Click Discovery` in `AppCatalogueAdmin`:

1. Validate installer path
2. Restore checkpoint `CleanState`
3. Start VM `AppCatalogueLab01`
4. Wait for guest heartbeat
5. Copy installer + discovery job + scripts into guest (`C:\Discovery\...`)
6. Wait for guest watcher to process job and power off VM
7. Monitor guest progress via Hyper-V KVP status signals (PowerShell-readable host communication)
8. Collect `discovery-results.json` from guest disk into:
   - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\guest-artifacts\`
   - plus compatibility copy to `C:\Installers\Discovery\Results\<JobId>\`
9. Show suggested silent switches, primary detection, secondary detection, evidence
10. Attempt VM cleanup/revert to `CleanState`

### 5) What still must be done manually

- Build/maintain the packaging VM itself
- Complete one-time in-guest bootstrap install
- Review and confirm suggestions before clicking `Apply Suggestions` / saving app config
- Troubleshoot installer-specific edge cases where silent detection is inconclusive

### 6) Troubleshooting

- `VM not found`: verify VM name is exactly `AppCatalogueLab01`
- `You do not have the required permission` / Hyper-V query fails:
  - run `AppCatalogueAdmin` as Administrator
  - add user to local `Hyper-V Administrators` group
  - sign out/in after group membership change
- `Checkpoint not found`: ensure `CleanState` exists
- Timeout waiting for results:
  - guest bootstrap task likely missing/not running
  - run `Install-DiscoveryBootstrap.ps1` again in guest
  - recreate `CleanState` checkpoint
- `No guest discovery status signal was received`:
  - watcher bootstrap is not active in the checkpoint
  - in guest run `C:\Discovery\Scripts\Install-DiscoveryBootstrap.ps1`
  - confirm scheduled task `AppCatalogueDiscoveryWatcher` is running
  - create a new `CleanState` checkpoint after that
- `Guest processing in progress` repeats:
  - open host job folder and inspect `host.log` + `status.json`
  - inspect guest artifacts (if present) under `guest-artifacts\Jobs\<JobId>\`
  - check `guest.log` for last stage reached
- `Guest script package not found`:
  - verify `DiscoveryScripts\Guest` is present near `AppCatalogueAdmin.exe`
  - republish with `publish_all.bat`
- Missing result JSON:
  - check per-job host log and status:
    - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\host.log`
    - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\status.json`
  - check guest logs:
    - `C:\Discovery\Logs\Discovery-Watcher.log`
    - `C:\Discovery\Output\Jobs\<JobId>\guest.log`
  - check admin app log:
    - `C:\Installers\Logs\AppCatalogueAdmin.log`

### 7) Helper scripts

- `Prepare-DiscoveryHost.ps1`
  - Ensures host discovery folders and stages guest script package
- `Prepare-DiscoveryVmGuest.ps1`
  - Copies guest scripts into running VM without requiring guest password entry
- `Invoke-DiscoveryMode.ps1`
  - Optional host-side troubleshooting runner for discovery flow outside the UI

## Validation Rules

- `Name` required and unique
- `InstallerSourceType` required
- `FileServer` entries require `InstallerPath`
- `Winget` entries require `WingetId`
- `PrimaryDetection.Type` and `PrimaryDetection.Value` required
- `SecondaryDetection` is optional but must be valid if populated

## Build

```powershell
dotnet restore .\AppCatalogueSuite.sln
dotnet build .\AppCatalogueSuite.sln -c Release
```

## Publish EXEs

Run:

```text
publish_all.bat
```

Output:

- `.\Published\AppCatalogue\AppCatalogue.exe`
- `.\Published\AppCatalogueAdmin\AppCatalogueAdmin.exe`

These are self-contained `win-x64` single-file publishes, so target machines do not need a separate .NET runtime install.

## Future Production Switch

When you move to production, update paths from local `C:\Installers\...` to your network location in config/admin workflow (for example `\\fileserver\...`) and redeploy the config as needed.
