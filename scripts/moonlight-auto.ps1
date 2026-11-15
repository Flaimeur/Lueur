# A lancer en administrateur. Installe (ou retire avec -Remove) la tache "Lueur - Moonlight" :
# a chaque demarrage du PC, moonlight-apollo.ps1 remet la liste Moonlight a jour (jeux Steam
# installes ou desinstalles). Apollo n'est redemarre que si la liste a change.
#   powershell -ExecutionPolicy Bypass -File moonlight-auto.ps1           -> installe
#   powershell -ExecutionPolicy Bypass -File moonlight-auto.ps1 -Remove   -> retire
param([switch]$Remove)

$name = 'Lueur - Moonlight'
if ($Remove) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    "Tache '$name' retiree."
    Start-Sleep 3
    return
}

$script = Join-Path $PSScriptRoot 'moonlight-apollo.ps1'
$lueur = Join-Path (Split-Path $PSScriptRoot) 'dist\Lueur.exe'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -Lueur `"$lueur`""
# 1 min apres le demarrage : Apollo et Steam ont le temps de se lancer.
$trigger = New-ScheduledTaskTrigger -AtStartup
$trigger.Delay = 'PT1M'
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
"Tache '$name' installee : la liste Moonlight se met a jour a chaque demarrage du PC."
Start-Sleep 3
