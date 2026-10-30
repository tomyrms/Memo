# Memo 0.1.0

Première version publique de Memo, application Windows locale pour importer, conserver et redécouvrir des Souvenirs Snapchat.

## Points forts

- **Import multi-ZIP** avec reprise, réimport sans doublon, espaces sources séparés et diagnostics par élément.
- **Bibliothèque réelle** : catalogue SQLite, photos et vidéos, dates avec provenance, correction manuelle, fuseau de classement.
- **Galerie chronologique** par mois, filtres, vue « Date inconnue », éléments à compléter.
- **Lecteur Story** photo/vidéo avec calques alignés, progression, clavier, son, lecture avec ou sans calque. Vidéo HEVC lue via LibVLC, sans dépendre de l'extension Microsoft.
- **« Ce jour-là »**, « Une journée à redécouvrir », « Surprends-moi ».
- **Albums, favoris, exclusions** persistés, conservés après réimport.
- **Récapitulatifs** annuels, mensuels et saisonniers, modifiables et enregistrables.
- **Recherche** texte (dont OCR local), visuelle par empreinte, groupes de visages indicatifs.
- **Carte hors ligne** des souvenirs disposant d'un lieu explicite.
- **Sauvegarde/restauration portable** et export d'une sélection.
- **Export MP4** d'une Story via Media Foundation.
- **Rappels Windows** facultatifs et discrets, via une tâche planifiée visible.

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
- 39 tests Core + 69 tests Media au moment de la release.

## Limites connues

- **Installateur non signé** : SmartScreen affiche un avertissement ; aucun certificat n'est simulé.
- **Validation sur machine Windows propre non réalisée** : lecture HEVC sans extension Microsoft, WebP, installation sans SDK/VC++ restent à confirmer sur VM neuve.
- **Index Snapchat avec liens distants** : non pris en charge, aucun schéma réel validé.
- **OCR** : dépend d'un pack de langue Windows ; l'absence est signalée.
- **Visages** : groupes indicatifs (empreinte 64 bits), pas des identités certaines ; renommage/fusion dans l'interface non terminés.
- **Récapitulatifs** : réordonnancement manuel et prévisualisation avant enregistrement non terminés.
- **Export** : H.264 via Media Foundation ; Windows N nécessite le Media Feature Pack ; pas de prévisualisation avant génération.
- **Rappels** : la tâche ne s'exécute que si Windows est allumé et l'utilisateur connecté ; l'affichage du toast dépend des réglages Windows.
- **ARM64** non pris en charge.
- **Licence du code Memo** : à définir avant diffusion sous licence.

## Vérifications effectuées

- Build Release, tests Core/Media, import UI de 6 ZIP (22 souvenirs), sauvegarde/restauration de 23 souvenirs, analyse locale (13 OCR, 13 empreintes), lecture HEVC et photo avec calque, export MP4 auto-vérifié, installateur compilé et testé en installation/désinstallation silencieuse.
- Captures d'écran des écrans principaux en 1320×860 et 1040×1800.

## Artefacts

- `Memo-0.1.0-win-x64-setup.exe` (non signé)
- `Memo-0.1.0-win-x64-setup.exe.sha256`

Les détails de procédure sont dans `docs/release.md` et `docs/install.md`.
