# Hyper-V Discovery Mode Setup Checklist

Use this quick checklist once per packaging VM.

## Required names

- VM Name: `AppCatalogueLab01`
- Checkpoint Name: `CleanState`
- Guest Username: `DiscoveryAdmin`

## Host checklist

1. Run PowerShell as Administrator on host.
2. From repo root, run:
   - `.\Prepare-DiscoveryHost.ps1`
   - `.\Prepare-DiscoveryVmGuest.ps1`
3. Confirm these folders exist:
   - `C:\Installers\Discovery\HostStaging\`
   - `C:\Installers\Discovery\Results\`
   - `C:\Installers\Discovery\Scripts\Guest\`

## Guest one-time checklist (inside VM)

1. Sign in as `DiscoveryAdmin`.
2. Open elevated PowerShell.
3. Run:
   - `C:\Discovery\Scripts\Install-DiscoveryBootstrap.ps1`
4. Verify scheduled task exists/running:
   - `AppCatalogueDiscoveryWatcher`
5. Create or refresh checkpoint named `CleanState`.

## AppCatalogueAdmin usage

1. Open `AppCatalogueAdmin`.
2. Select/import installer (FileServer type).
3. Confirm VM settings:
   - VM = `AppCatalogueLab01`
   - Checkpoint = `CleanState`
4. Click `One-Click Discovery`.
5. Review:
   - Silent suggestions
   - Recommended silent command + confidence
   - Attempt history
   - Primary detection recommendation
   - Secondary detection recommendation
   - Evidence
6. Click `Apply Suggestions` only if results look correct.
7. Save app entry/config.

## If discovery fails

1. Open the discovery job folder from AppCatalogueAdmin (`Open Job Folder`).
2. Check host per-job artifacts:
   - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\host.log`
   - `C:\ProgramData\AppCatalogue\Discovery\Jobs\<JobId>\status.json`
3. Check collected guest per-job artifacts:
   - `...\guest-artifacts\Jobs\<JobId>\guest.log`
   - `...\guest-artifacts\Jobs\<JobId>\discovery-status.json`
   - `...\guest-artifacts\Jobs\<JobId>\discovery-results.json`
4. Check watcher service log inside VM:
   - `C:\Discovery\Logs\Discovery-Watcher.log`
5. Re-run guest bootstrap script and recreate `CleanState` if watcher or completion signaling is missing.
6. Keep `C:\Installers\Logs\AppCatalogueAdmin.log` for overall app-level diagnostics.
