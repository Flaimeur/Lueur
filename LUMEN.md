# Piloter Lueur depuis Lumen (ou toute autre appli)

Lueur s'intègre sans dépendance : une ligne de commande pour agir, un fichier JSON pour lire l'état.

## Trouver Lueur

Dans l'ordre :

1. la valeur `Lueur` de `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (présente si « Lancer avec Windows » est actif) — chemin entre guillemets suivi de `--minimized` ;
2. le champ `exe` de `%APPDATA%\Lueur\state.json` (écrit par Lueur à chaque changement) ;
3. les emplacements habituels : `%LOCALAPPDATA%\Programs\Lueur\Lueur.exe`, `Bureau\Lueur\dist\Lueur.exe`.

## Agir : la ligne de commande

`Lueur.exe <commandes>` — plusieurs commandes possibles, exécutées dans l'ordre.

| Commande | Effet |
|---|---|
| `--on` / `--off` / `--toggle` | chambre + PC |
| `--warm` | lumière de soirée (blanc 2200 K, 30 %) |
| `--scene "Nom"` | ambiance enregistrée dans Lueur (liste dans `state.json` → `scenes`) |
| `--color #RRGGBB` | couleur exacte (la luminosité suit la couleur) |
| `--level 0-100` | luminosité, couleur inchangée |
| `--adaptive auto\|sun\|rain\|cloud\|off` | éclairage adaptatif (profil selon la météo ou imposé) |
| `--screen` | mode écran (les lumières suivent l'image) |
| `--show` | ouvre la fenêtre de Lueur |

- Si Lueur est ouvert, la commande lui est transmise (tube nommé `Lueur.Lights.Commands`, réservé à l'utilisateur courant) : il l'exécute aussitôt, dans l'ordre d'arrivée, et le processus lancé se ferme en moins d'une seconde.
- S'il est fermé, `--on/--off/--toggle/--warm/--scene/--color/--level` s'exécutent sans fenêtre puis le processus se termine (code de sortie 0, 1 en cas d'échec) ; `--screen/--adaptive/--show` ouvrent Lueur réduit.

## Lire l'état : `%APPDATA%\Lueur\state.json`

Réécrit au plus deux fois par seconde quand quelque chose change (écriture atomique : `.tmp` puis renommage).

```json
{
  "app": "Lueur",
  "version": "1.1.0",
  "exe": "C:\\...\\Lueur.exe",
  "running": true,
  "on": true,
  "color": "#7700FF",
  "level": 85,
  "description": "Violet · 85 %",
  "mode": "manual",
  "adaptiveProfile": null,
  "roomLights": 2,
  "pcDevices": 3,
  "scenes": ["Gaming", "Cosy", "Focus"],
  "commands": ["--on", "--off", "..."],
  "updatedAt": "2026-11-15T21:04:12+01:00"
}
```

- `mode` : `manual`, `adaptive` (avec `adaptiveProfile` = `sun`, `rain` ou `cloud`) ou `screen`.
- `color` est la teinte pleine ; l'intensité réelle est `level` (et `on`).
- `running: false` quand Lueur a été quitté.
- `settings.json` reste lisible pour compatibilité (`Look`), mais `state.json` est la source à privilégier : il dit si c'est allumé, et on n'y trouve jamais de secret (le jeton de la passerelle IKEA reste chiffré dans `settings.json`).

## Suggestions pour le widget Lumen

- Remplacer la liste d'actions fixe par `scenes` de `state.json` (une action `--scene "Nom"` par ambiance) en plus de Allumer / Éteindre / Soirée.
- Afficher `description` et une pastille `color` estompée quand `on` vaut `false`.
- Pour le mode soirée : `--warm` à l'entrée, et `--adaptive auto` le matin si l'utilisateur utilise l'adaptatif.
