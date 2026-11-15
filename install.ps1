#requires -Version 5.1
<#
    Remote Guard installer and updater.
    Run in PowerShell 5.1+ or PowerShell 7+:

        powershell -c "irm https://raw.githubusercontent.com/XYphrodite/RemoteGuard/main/install.ps1 | iex"
#>
& {
    [CmdletBinding()]
    param(
        [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\RemoteGuard'),
        [string] $Version = 'latest'
    )

    $ErrorActionPreference = 'Stop'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $repo = 'XYphrodite/RemoteGuard'
    $asset = 'RemoteGuard.zip'
    $headers = @{ 'User-Agent' = 'remoteguard-installer/1.0' }

    $desktopRuntime = & dotnet --list-runtimes 2>$null |
        Where-Object { $_ -like 'Microsoft.WindowsDesktop.App 10.*' }
    if (-not $desktopRuntime) {
        throw '.NET 10 Desktop Runtime not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
    }

    if ($Version -eq 'latest') {
        $zipUrl = "https://github.com/$repo/releases/latest/download/$asset"
        $shaUrl = "https://github.com/$repo/releases/latest/download/$asset.sha256"
    } else {
        $tag = if ($Version -match '^v') { $Version } else { "v$Version" }
        $zipUrl = "https://github.com/$repo/releases/download/$tag/$asset"
        $shaUrl = "https://github.com/$repo/releases/download/$tag/$asset.sha256"
    }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('remoteguard-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $tmp | Out-Null
        $zip = Join-Path $tmp $asset
        $sha = Join-Path $tmp "$asset.sha256"

        Write-Output "Downloading $zipUrl ..."
        Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing -Headers $headers
        Invoke-WebRequest -Uri $shaUrl -OutFile $sha -UseBasicParsing -Headers $headers

        $shaText = [IO.File]::ReadAllText($sha)
        if ($shaText -notmatch '([0-9a-fA-F]{64})') {
            throw 'Failed to parse SHA-256 checksum from release sidecar.'
        }
        $expectedHash = $Matches[1].ToLowerInvariant()

        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $fs = [IO.File]::OpenRead($zip)
            try {
                $hashBytes = $sha256.ComputeHash($fs)
            } finally {
                $fs.Dispose()
            }
        } finally {
            $sha256.Dispose()
        }
        $actualHash = ([BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
        if ($actualHash -ne $expectedHash) {
            throw "SHA-256 mismatch: expected $expectedHash, got $actualHash."
        }
        Write-Output 'SHA-256 verified.'

        $stage = Join-Path $tmp 'app'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $stage)

        $stagedExe = Join-Path $stage 'RemoteGuard.exe'
        if (-not (Test-Path -LiteralPath $stagedExe)) {
            throw 'RemoteGuard.exe was not found in the release archive.'
        }

        $probe = Start-Process -FilePath $stagedExe -ArgumentList '--help' -Wait -PassThru -WindowStyle Hidden
        if ($probe.ExitCode -ne 0) {
            throw "Health probe (--help) failed with exit code $($probe.ExitCode)."
        }

        $running = @(Get-Process -Name 'RemoteGuard' -ErrorAction SilentlyContinue)
        if ($running.Count -gt 0) {
            Write-Output 'Stopping running Remote Guard instance...'
            $running | Stop-Process -Force
            Start-Sleep -Milliseconds 500
        }

        Write-Output "Installing to $InstallDir ..."
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Get-ChildItem -LiteralPath $InstallDir -Filter 'RemoteGuard.exe.old-*' -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
        Copy-Item -Path (Join-Path $stage '*') -Destination $InstallDir -Recurse -Force

        $ws = New-Object -ComObject WScript.Shell
        $exe = Join-Path $InstallDir 'RemoteGuard.exe'
        $desktopLink = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Remote Guard.lnk'))
        $desktopLink.TargetPath = $exe
        $desktopLink.WorkingDirectory = $InstallDir
        $desktopLink.Save()
        Write-Output 'Desktop shortcut updated.'

        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($isAdmin) {
            try {
                $svc = New-Object -ComObject Schedule.Service
                $svc.Connect()
                $rootFolder = $svc.GetFolder('\')
                $td = $svc.NewTask(0)
                $td.RegistrationInfo.Description = 'Start Remote Guard in system tray with elevated privileges at logon.'
                $td.Principal.UserId = "$env:USERDOMAIN\$env:USERNAME"
                $td.Principal.LogonType = 3 # TASK_LOGON_INTERACTIVE_TOKEN
                $td.Principal.RunLevel = 1  # TASK_RUNLEVEL_HIGHEST
                $td.Settings.Enabled = $true
                $td.Settings.DisallowStartIfOnBatteries = $false
                $td.Settings.StopIfGoingOnBatteries = $false
                $td.Settings.ExecutionTimeLimit = 'PT0S'
                $td.Settings.MultipleInstances = 2 # TASK_INSTANCES_IGNORE_NEW
                $trigger = $td.Triggers.Create(9) # TASK_TRIGGER_LOGON
                $trigger.UserId = "$env:USERDOMAIN\$env:USERNAME"
                $action = $td.Actions.Create(0) # TASK_ACTION_EXEC
                $action.Path = $exe
                $action.Arguments = '-Tray'
                $action.WorkingDirectory = $InstallDir
                $rootFolder.RegisterTaskDefinition('RemoteGuardTray', $td, 6, $null, $null, 3) | Out-Null
                Write-Output "Autostart task 'RemoteGuardTray' configured (tray, elevated)."
            } catch {
                Write-Warning "Failed to configure autostart task: $($_.Exception.Message)"
            }
        }

        if ([Environment]::UserInteractive) {
            Start-Process -FilePath $exe
            Write-Output 'Remote Guard started.'
        } else {
            Write-Output 'Non-interactive session: launch Remote Guard from the Desktop shortcut.'
        }
        Write-Output 'Installation complete.'
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }
} @args
