#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$RemoveData)

$ErrorActionPreference = 'Continue'
$taskName = 'StealthEye Session'
$serviceName = 'StealthEye'

Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
& sc.exe stop $serviceName | Out-Null
& sc.exe delete $serviceName | Out-Null
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $env:ProgramFiles 'StealthEye') -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) {
    Remove-Item (Join-Path $env:ProgramData 'StealthEye') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA 'StealthEye') -Recurse -Force -ErrorAction SilentlyContinue
}
