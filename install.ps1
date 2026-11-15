#requires -Version 5.1
<#
    Установщик и обновлятор Remote Guard.
    Запуск (PowerShell 5.1+):

        powershell -c "irm https://raw.githubusercontent.com/XYphrodite/RemoteGuard/main/install.ps1 | iex"

    Скачивает релиз RemoteGuard.zip с GitHub Releases, проверяет SHA-256 и
    работоспособность бинарника (--help), останавливает запущенный экземпляр,
    устанавливает в %LOCALAPPDATA%\Programs\RemoteGuard, создаёт ярлык на рабочем столе
    (и задачу автозапуска в трее при наличии прав администратора) и запускает приложение.
    Требуется .NET 10 Desktop Runtime.
#>
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
    throw '.NET 10 Desktop Runtime не найден. Установи его: https://dotnet.microsoft.com/download/dotnet/10.0'
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

    Write-Output "Скачиваю $zipUrl ..."
    Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing -Headers $headers
    Invoke-WebRequest -Uri $shaUrl -OutFile $sha -UseBasicParsing -Headers $headers

    $shaText = [IO.File]::ReadAllText($sha)
    if ($shaText -notmatch '([0-9a-fA-F]{64})') {
        throw 'Не удалось прочитать контрольную сумму SHA-256 релиза.'
    }
    $expectedHash = $Matches[1].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Не совпал SHA-256 архива: ожидался $expectedHash, получен $actualHash."
    }
    Write-Output 'SHA-256 проверен.'

    $stage = Join-Path $tmp 'app'
    Expand-Archive -LiteralPath $zip -DestinationPath $stage
    $stagedExe = Join-Path $stage 'RemoteGuard.exe'
    if (-not (Test-Path -LiteralPath $stagedExe)) {
        throw 'В архиве релиза не найден RemoteGuard.exe.'
    }

    $probe = Start-Process -FilePath $stagedExe -ArgumentList '--help' -Wait -PassThru -WindowStyle Hidden
    if ($probe.ExitCode -ne 0) {
        throw "Проверка нового RemoteGuard.exe не прошла (код $($probe.ExitCode))."
    }

    $running = @(Get-Process -Name 'RemoteGuard' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Output 'Останавливаю запущенный Remote Guard перед обновлением...'
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    Write-Output "Устанавливаю в $InstallDir ..."
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
    Write-Output 'Ярлык на рабочем столе обновлён.'

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        try {
            $taskName = 'RemoteGuardTray'
            $action = New-ScheduledTaskAction -Execute $exe -Argument '-Tray' -WorkingDirectory $InstallDir
            $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
            $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
            Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
            Write-Output "Автозапуск в трее настроен (задача '$taskName')."
        } catch {
            Write-Warning "Не удалось настроить задачу автозапуска: $($_.Exception.Message)"
        }
    }

    if ([Environment]::UserInteractive) {
        Start-Process -FilePath $exe
        Write-Output 'Remote Guard запущен.'
    } else {
        Write-Output 'Сессия неинтерактивна — приложение не запущено. Запусти ярлык Remote Guard на рабочем столе.'
    }
    Write-Output 'Установка завершена.'
} finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}
