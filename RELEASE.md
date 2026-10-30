# Memo 0.1.0

Première version publique de Memo, application Windows locale pour importer, conserver et redécouvrir des Souvenirs Snapchat.

## Points forts

- **Import multi-ZIP et dossier complet** avec reprise, réimport sans doublon, espaces sources séparés, contrôle de l'espace disque et diagnostics par élément. Le disque de la bibliothèque se choisit directement depuis l'écran Imports, sans perdre la sélection d'archives.
- **Bibliothèque réelle** : catalogue SQLite versionné, photos et vidéos, dates avec provenance, correction manuelle, fuseau de classement.
- **Galerie chronologique** par mois, filtres, vue « Date inconnue », éléments à compléter.
- **Lecteur Story** photo/vidéo avec calques alignés, progression, clavier, volume 0–100, muet synchronisé, heure exacte quand elle est connue, lecture avec ou sans calque. Vidéo HEVC lue via LibVLC, sans dépendre de l'extension Microsoft.
- **Enregistrement du média courant** sans réencodage (média et calque séparés, JSON de métadonnées, SHA-256) et **export MP4** d'une Story.
- **« Ce jour-là »**, « Une journée à redécouvrir », « Surprends-moi ».
- **Albums éditables** (ajout, retrait, réordonnancement, lecture dans l'ordre), favoris et exclusions persistés, conservés après réimport.
- **Récapitulatifs** annuels, mensuels et saisonniers, modifiables et enregistrables.
- **Recherche harmonisée** texte (dont OCR local), lieu, date et identifiant ; recherche visuelle par empreinte dans l'ordre de similarité ; groupes de visages indicatifs.
- **Carte hors ligne** des souvenirs disposant d'un lieu explicite.
- **Sauvegarde/restauration portable** avec progression, annulation et vérification SHA-256 des médias.
- **Rappels Windows** facultatifs et discrets, via une tâche planifiée visible, ouvrant la bonne bibliothèque par identifiant.

## Installation

Windows 10 21H2 (build 19041) ou Windows 11, x64. Installateur non signé :

1. Lancer `Memo-0.1.0-win-x64-setup.exe`.
2. Vérifier le SHA-256 publié.
3. Accepter l'avertissement SmartScreen si nécessaire.
4. Créer une bibliothèque dans **Réglages → Bibliothèque**, puis importer les ZIP dans **Imports**.

## Corrections et robustesse

- Les sources importées ne sont jamais réécrites ; miniatures et compositions sont des dérivés.
- WebP sous extension `.png` reconnu par signature.
- Rotation appliquée une seule fois ; alignement des calques dans le repère affiché.
- Archives corrompues, entrées `../`, conflits de rôle et médias manquants produisent un diagnostic sans bloquer l'application.
- L'installateur inclut désormais `Memo.pri` et les fichiers XAML compilés (`.xbf`) ; l'application installée démarre réellement, ce qui n'était pas le cas de la première archive publiée.
- 61 tests Core, 69 tests Media et 12 tests d’intégration au moment de la release (142 au total).

## Limites connues

- **Installateur non signé** : SmartScreen affiche un avertissement ; aucun certificat n'est simulé.
- **Validation sur machine Windows propre non réalisée** : lecture HEVC sans extension Microsoft, WebP, installation sans SDK/VC++ restent à confirmer sur VM neuve.
- **Index Snapchat avec liens distants** : non pris en charge, aucun schéma réel validé.
- **OCR** : dépend d'un pack de langue Windows ; l'absence est signalée.
- **Visages** : groupes indicatifs (empreinte 64 bits), pas des identités certaines ; renommage/fusion dans l'interface non terminés.
- **Récapitulatifs** : les périodes proposées dépendent des années réellement datées ; aucune génération inventée pour les médias absents.
- **Export** : H.264 via Media Foundation ; Windows N nécessite le Media Feature Pack. L'enregistrement du média courant, lui, ne réencode rien.
- **Rappels** : la tâche ne s'exécute que si Windows est allumé et l'utilisateur connecté ; l'affichage du toast dépend des réglages Windows.
- **Import** : la bascule vers un autre disque conserve la sélection d'archives par instantané, mais le parcours complet 17 ZIP → nouveau disque reste à rejouer manuellement sur l'export réel.
- **ARM64** non pris en charge.
- **Licence du code Memo** : à définir avant diffusion sous licence.

## Vérifications effectuées

- Build Release, tests Core/Media, import UI de 6 ZIP (22 souvenirs), sauvegarde/restauration de 23 souvenirs, analyse locale (13 OCR, 13 empreintes), lecture HEVC et photo avec calque, export MP4 auto-vérifié.
- Installateur 0.1.0 reconstruit après la passe de correction : installation silencieuse dans `%LOCALAPPDATA%\Programs\Memo`, démarrage réel de la version installée sur la bibliothèque de 838 souvenirs, désinstallation silencieuse sans résidu du dossier d'installation.
- Publication vérifiée : `Memo.pri` et 17 fichiers `.xbf` présents avant compilation de l'installateur.
- Dialogue « Choisir un autre disque » vérifié à l'écran : liste des disques avec espace libre, nom de dossier, aperçu du chemin et création d'une bibliothèque sur `X:`.
- Écran Imports revu à 1040×1800 et 1320×860 : le bouton de choix du disque reste entièrement visible.
- Captures d'écran des écrans principaux en 1320×860 et 1040×1800.

## Artefacts

- `Memo-0.1.0-win-x64-setup.exe` (non signé)
- `Memo-0.1.0-win-x64-setup.exe.sha256`

Les détails de procédure sont dans `docs/release.md` et `docs/install.md`.
