# A lancer en administrateur. Installe (ou retire avec -Remove) la tache "Lueur - OpenRGB" :
# OpenRGB demarre en administrateur (necessaire pour la RAM, via le pilote PawnIO) a l'ouverture de session,
# sans demande d'autorisation. Lueur s'en sert aussi pour relancer OpenRGB s'il s'arrete.
#   powershell -ExecutionPolicy Bypass -File openrgb-admin.ps1           -> installe
#   powershell -ExecutionPolicy Bypass -File openrgb-admin.ps1 -Remove   -> retire
param([switch]$Remove, [string]$User = "$env:USERDOMAIN\$env:USERNAME")

$name = 'Lueur - OpenRGB'
if ($Remove) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    "Tache '$name' retiree."
    Start-Sleep 3
    return
}

$exe = 'C:\Program Files\OpenRGB\OpenRGB.exe'
if (-not (Test-Path $exe)) { throw "OpenRGB introuvable : $exe" }
$action = New-ScheduledTaskAction -Execute $exe -Argument '--server --startminimized' -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
"Tache '$name' installee : OpenRGB demarre en administrateur a chaque ouverture de session."
Start-Sleep 3
