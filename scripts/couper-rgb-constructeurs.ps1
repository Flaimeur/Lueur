# A lancer en administrateur. Empeche iCUE (Corsair) et Mystic Light (MSI) de reprendre la main
# sur le RGB, pour qu'OpenRGB/Lueur soit seul a piloter la RAM et la carte mere.
# Rien n'est desinstalle : les services passent en "Manuel" et iCUE ne demarre plus avec Windows.
# Pour revenir en arriere : remettre ces services en "Automatique" dans services.msc.

$services = 'CorsairService', 'Corsair.Service', 'CorsairDeviceControlService', 'CorsairCpuIdService',
            'CorsairDeviceListerService', 'iCUEUpdateService', 'Mystic_Light_Service'

Get-Process iCUE, iCUEDevicePluginHost -ErrorAction SilentlyContinue | Stop-Process -Force

foreach ($name in $services) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) { $svc = Get-Service -DisplayName $name -ErrorAction SilentlyContinue }
    foreach ($s in @($svc)) {
        if (-not $s) { continue }
        Stop-Service $s.Name -Force -ErrorAction SilentlyContinue
        Set-Service $s.Name -StartupType Manual
        "Service coupe : $($s.Name)"
    }
}

# Demarrage automatique d'iCUE (entree Run)
foreach ($key in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run') {
    $props = Get-ItemProperty $key -ErrorAction SilentlyContinue
    if (-not $props) { continue }
    foreach ($p in $props.PSObject.Properties | Where-Object { $_.Value -is [string] -and $_.Value -match 'iCUE' }) {
        Remove-ItemProperty $key -Name $p.Name
        "Demarrage auto retire : $($p.Name)"
    }
}

"Termine. Relance Lueur (ou clique Reconnecter OpenRGB)."
Start-Sleep 4
