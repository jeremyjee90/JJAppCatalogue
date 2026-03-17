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
- Includes `Explain Detection Rule`, `Test Detection`, and optional `Discovery Mode` actions
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
- `Discovery Mode` (optional packaging flow) captures pre/post install snapshots and proposes detection rules from observed changes
- Discovery results are best-effort only and must be reviewed manually

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
