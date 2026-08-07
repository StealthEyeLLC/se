#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release

$source = Join-Path $root 'artifacts\publish'
$install = Join-Path $env:ProgramFiles 'StealthEye'
$data = Join-Path $env:ProgramData 'StealthEye'
$systemProcesses = Join-Path $data 'run\processes'
$userData = Join-Path $env:LOCALAPPDATA 'StealthEye'
$userProcesses = Join-Path $userData 'run\processes'
$configPath = Join-Path $data 'config.json'
$serviceName = 'StealthEye'
$taskName = 'StealthEye Session'
$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$owner = $currentIdentity.Name
$ownerSid = $currentIdentity.User
$systemSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$administratorsSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$eye = Join-Path $install 'eye.exe'

function Test-InstallTreeCurrent {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) { return $false }
    foreach ($sourceFile in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        $relative = $sourceFile.FullName.Substring($Source.Length).TrimStart('\\')
        $target = Join-Path $Destination $relative
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { return $false }
        $targetFile = Get-Item -LiteralPath $target
        if ($targetFile.Length -ne $sourceFile.Length) { return $false }
        if ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { return $false }
    }
    return $true
}

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existingService -and $existingTask -and (Test-InstallTreeCurrent -Source $source -Destination $install)) {
    $healthy = $false
    try {
        $health = Invoke-RestMethod -Uri 'http://127.0.0.1:37921/healthz' -UseBasicParsing -TimeoutSec 2
        $healthy = [bool]$health.ok
    } catch {}

    if ($healthy) {
        $sessionOutput = & $eye call session.info 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Output "Already installed $eye"
            Write-Output 'Service health: OK'
            Write-Output 'Session pipe: OK'
            Write-Output 'MCP endpoint: http://127.0.0.1:37921/mcp'
            return
        }
    }
}

New-Item -ItemType Directory -Force -Path $install, $data, (Join-Path $data 'run'), (Join-Path $data 'logs'), $systemProcesses, $userProcesses | Out-Null

if (-not (Test-Path $configPath)) {
    $tokenBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($tokenBytes) } finally { $rng.Dispose() }
    $token = [Convert]::ToBase64String($tokenBytes)
    $config = @{
        listen_address = '127.0.0.1'
        port = 37921
        mcp_path = '/mcp'
        local_token = $token
        pipe_name = 'StealthEye.Session'
        process_output_directory = $systemProcesses
        user_process_output_directory = $userProcesses
    } | ConvertTo-Json -Depth 5
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($configPath, $config, $utf8)
}

$configAcl = New-Object System.Security.AccessControl.FileSecurity
$configAcl.SetAccessRuleProtection($true, $false)
$configAcl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($systemSid, [System.Security.AccessControl.FileSystemRights]::FullControl, [System.Security.AccessControl.AccessControlType]::Allow))
$configAcl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($administratorsSid, [System.Security.AccessControl.FileSystemRights]::FullControl, [System.Security.AccessControl.AccessControlType]::Allow))
$configAcl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($ownerSid, [System.Security.AccessControl.FileSystemRights]::ReadAndExecute, [System.Security.AccessControl.AccessControlType]::Allow))
Set-Acl -LiteralPath $configPath -AclObject $configAcl

Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe stop $serviceName | Out-Null
    & sc.exe delete $serviceName | Out-Null
    for ($i = 0; $i -lt 40 -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue); $i++) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        throw "Existing $serviceName service is still pending deletion."
    }
}

$installPrefix = $install.TrimEnd('\') + '\'
Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -like ($installPrefix + '*') } catch { $false }
} | Stop-Process -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 40; $i++) {
    $locked = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -like ($installPrefix + '*') } catch { $false }
    }
    if (-not $locked) { break }
    Start-Sleep -Milliseconds 250
}
if (Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like ($installPrefix + '*') } catch { $false } }) {
    throw "Existing StealthEye processes are still holding files under $install."
}

Copy-Item (Join-Path $source '*') $install -Recurse -Force

$binary = '"' + $eye + '" serve'
New-Service -Name $serviceName -BinaryPathName $binary -StartupType Automatic -DisplayName 'StealthEye' | Out-Null
& sc.exe description $serviceName 'StealthEye laptop-native MCP server' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed with exit code $LASTEXITCODE" }
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed with exit code $LASTEXITCODE" }

$action = New-ScheduledTaskAction -Execute $eye -Argument 'session' -WorkingDirectory $install
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $owner
$principal = New-ScheduledTaskPrincipal -UserId $owner -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

& sc.exe start $serviceName | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe start failed with exit code $LASTEXITCODE" }
Start-ScheduledTask -TaskName $taskName

$healthy = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        $health = Invoke-RestMethod -Uri 'http://127.0.0.1:37921/healthz' -UseBasicParsing -TimeoutSec 2
        if ($health.ok) { $healthy = $true; break }
    } catch {}
    Start-Sleep -Milliseconds 500
}
if (-not $healthy) { throw 'StealthEye service did not become healthy.' }

$sessionOutput = & $eye call session.info 2>&1
if ($LASTEXITCODE -ne 0) { throw "StealthEye session helper did not respond: $sessionOutput" }

Write-Output "Installed $eye"
Write-Output 'Service health: OK'
Write-Output 'Session pipe: OK'
Write-Output 'MCP endpoint: http://127.0.0.1:37921/mcp'
