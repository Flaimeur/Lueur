# Lueur

Une seule appli Windows pour la lumière de la chambre (IKEA DIRIGERA, en local, sans cloud) et le RGB du PC (OpenRGB).

- **Tout allumer / Tout éteindre** : chambre + PC en un clic, en raccourci clavier, depuis l'icône de la barre des tâches ou avec un `.bat`
- **Couleur, blanc chaud/froid, luminosité, ambiances** appliqués aux deux en même temps
- **Le PC suit la chambre** : si tu changes la lumière avec Siri, Alexa, l'app IKEA ou un interrupteur, le RGB du PC prend la même couleur (websocket temps réel de la DIRIGERA)
- **Ambiances à toi** : enregistre la couleur actuelle en ambiance (tuile « + »), clic droit pour la renommer, la remplacer ou la supprimer
- **Éclairage adaptatif, routines, météo, mode écran**, et pilotage depuis [Lumen](https://github.com/Flaimeur/Lumen) ou n'importe quel script ([LUMEN.md](LUMEN.md))

## Installer

1. Télécharge `Lueur.exe` dans la [dernière Release](https://github.com/Flaimeur/Lueur/releases/latest) (aucun .NET à installer).
2. Range-le où tu veux, par exemple `%LOCALAPPDATA%\Programs\Lueur\`, et lance-le.
3. Lueur trouve ta passerelle IKEA DIRIGERA : appuie sur son bouton ACTION quand il le demande.
4. Pour le RGB du PC, installe [OpenRGB](https://openrgb.org) (il démarre en service Windows et Lueur s'y connecte).
5. Roue dentée en haut à droite : ta ville (météo, lever et coucher du soleil) et les mises à jour.

Les mises à jour arrivent toutes seules : Lueur vérifie GitHub, télécharge la nouvelle version, vérifie son empreinte et l'installe au démarrage suivant (désactivable dans les réglages).

## Compiler soi-même

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Puis `dist\Lueur.exe`. Au premier lancement, Lueur trouve la DIRIGERA sur le réseau, tu cliques sur **Appairer** et tu appuies sur le bouton **ACTION** sous la passerelle (60 s pour le faire).

| Commande | Effet |
|---|---|
| `Lueur.exe` | fenêtre + icône dans la barre des tâches |
| `Lueur.exe --minimized` | icône seulement (utilisé par « Lancer avec Windows ») |
| `Lueur.exe --off` / `--on` / `--toggle` | action sans fenêtre, puis quitte |
| `Lueur.exe --warm` | lumière de soirée (blanc 2200 K, 30 %), utilisée par Lumen |
| Ctrl + Alt + Fin / Début | tout éteindre / tout allumer (quand Lueur tourne) |

## Comment ça marche

**DIRIGERA** (`Core/DirigeraClient.cs`) : API locale `https://<ip>:8443/v1`.
- Appairage OAuth PKCE : `GET /oauth/authorize` renvoie un code, tu appuies sur le bouton, `POST /oauth/token` renvoie le token d'accès.
- Le token est chiffré avec DPAPI dans `%APPDATA%\Lueur\settings.json` (lisible uniquement par ton compte Windows).
- Le certificat auto-signé de la passerelle est vérifié (`DIRIGERA HUB`) puis son empreinte SHA-256 est épinglée : aucun autre certificat n'est accepté ensuite.
- Commandes : `PATCH /devices/{id}` avec `[{"attributes":{"isOn":true}}]`, `lightLevel`, `colorHue` + `colorSaturation`, `colorTemperature`.
- Événements : websocket `wss://<ip>:8443/v1`, messages `deviceStateChanged`.

**OpenRGB** (`Core/OpenRgbService.cs`) : serveur SDK sur `127.0.0.1:6742`. Lueur le lance en `--server --startminimized` s'il ne tourne pas, passe chaque périphérique en mode Direct et pousse la même couleur sur toutes les LEDs (noir = éteint).

## OpenRGB : une seule fois

1. iCUE, G HUB et MSI Center ne doivent plus gérer le RGB, sinon ils se battent avec OpenRGB :
   - **iCUE** : ta seule pièce Corsair est la RAM. Quitte iCUE et retire-le du démarrage (Gestionnaire des tâches > Démarrage), ou désinstalle-le.
   - **MSI Center** : désinstalle le module Mystic Light (ou désactive le démarrage de MSI Center).
   - **G HUB** : tu peux le garder pour les réglages souris/clavier ; dans ses paramètres, décoche le contrôle de l'éclairage par les applis et choisis un éclairage fixe.
2. Lance OpenRGB **une fois en administrateur** : il installe son pilote SMBus pour la RAM et la carte mère, puis il scanne.
3. Dans OpenRGB > Settings : coche *Start at login*, *Start minimized* et *Start server*.

## Siri (Apple Maison) sans casser Alexa

La DIRIGERA est un pont Matter et Matter accepte plusieurs « maîtres » en même temps : Apple Maison s'ajoute à côté d'Alexa, sans rien retirer.

1. App **IKEA Home smart** > Réglages > **Intégrations** > **Matter** : active-la et génère un **code d'appairage**.
2. iPhone > app **Maison** > **+** > Ajouter un accessoire > *Autres options* > saisis le code.
3. Range les lampes dans la pièce « Chambre ». Ensuite : « Dis Siri, éteins la chambre ».

Lueur reçoit ces changements et fait suivre le PC (option « Le PC suit la chambre »).

## Yeti X : laissé à G HUB

OpenRGB pilote mal le Yeti X : le micro disparaît de la détection et n'affiche les couleurs qu'une fois sur deux. Il est donc exclu de Lueur (liste PC) et reste géré par G HUB, qui démarre normalement avec Windows. L'option « Fermer G HUB automatiquement » de Lueur est coupée ; la réactiver rend la main du micro à Lueur.

## OpenRGB : le service Windows

OpenRGB est installé avec un service Windows « OpenRGB » qui le démarre avec les droits système dès l'allumage (accès à la RAM via PawnIO). Lueur s'y connecte et n'en lance jamais un second : deux OpenRGB se disputent la carte mère et le second affiche l'avertissement SMBus. La tâche « Lueur - OpenRGB » (`scripts/openrgb-admin.ps1`) ne sert que si ce service n'existe pas.
