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
$owner = "$env:USERDOMAIN\$env:USERNAME"
$eye = Join-Path $install 'eye.exe'

New-Item -ItemType Directory -Force -Path $install, $data, (Join-Path $data 'run'), (Join-Path $data 'logs'), $systemProcesses, $userProcesses | Out-Null
Copy-Item (Join-Path $source '*') $install -Recurse -Force

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
$configAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('NT AUTHORITY\SYSTEM', 'FullControl', 'Allow')))
$configAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators', 'FullControl', 'Allow')))
$configAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($owner, 'ReadAndExecute', 'Allow')))
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

$binary = '"' + $eye + '" serve'
& sc.exe create $serviceName binPath= $binary start= auto obj= LocalSystem DisplayName= 'StealthEye' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE" }
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
