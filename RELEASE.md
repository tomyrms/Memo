# Memo 0.1.0

Date : 30 octobre 2026

Statut : **pré-version technique, non signée**. Ce document est un modèle de
notes de version ; il ne déclare pas terminées les fonctions qui n'ont pas été
validées sur une installation Windows propre.

## Fonctionnalités

- **Socle Windows** : application WinUI 3 non empaquetée, auto-contenue, cible
  Windows 10 21H2 / Windows 11 x64.
- **Moteur de bibliothèque** (`Memo.Core`) : catalogue SQLite, import de
  plusieurs archives, rapprochement des médias et calques, dates avec
  provenance et fuseau, découverte, sauvegarde et réglages. Ces briques sont
  couvertes par les tests du projet `Memo.Core`.
- **Moteur média** (`Memo.Media`) : détection des formats par signature
  (JPEG, PNG, WebP, MP4), orientation EXIF, composition d'images, miniatures
  WebP et extraction d'images vidéo via LibVLC. Ces briques sont couvertes par
  les tests du projet `Memo.Media`.
- **Interface de démonstration** : navigation, galerie, lecture Story, « Ce
  jour-là », albums, récapitulatifs, recherche, carte, écran Imports et
  Réglages, alimentés par un corpus synthétique explicite.
- **Installateur Windows** : Inno Setup 6, installation par utilisateur dans
  `%LOCALAPPDATA%\Programs\Memo`, raccourci menu Démarrer, raccourci bureau
  facultatif, désinstallation propre.
- **Licences tierces** : `LICENSES/` et `THIRD-PARTY-NOTICES.txt` installés
  avec l'application ; DLL LibVLC séparées et remplaçables.

## Corrections

Aucune correction issue d'une version publiée précédemment : 0.1.0 est la
première version préparée. Les éléments non terminés sont décrits dans les
limites connues plutôt que présentés comme corrigés.

## Limites connues

- **Import non accessible depuis l'interface** : l'écran Imports permet de
  sélectionner des archives, mais l'écriture dans une bibliothèque n'est pas
  encore reliée. L'application démarre en mode démonstration avec des médias
  synthétiques.
- **Création et ouverture de bibliothèque non reliées** : les boutons de
  l'écran Réglages affichent encore « non disponible ».
- **Installateur non signé** : SmartScreen peut afficher un avertissement.
  Aucune signature Authenticode n'est fournie.
- **Validation sur machine propre non réalisée** : un test local silencieux a
  vérifié l'installation, les DLL LibVLC, les licences et la désinstallation,
  mais l'installation sans SDK, la lecture HEVC sans extension Microsoft, le
  décodage WebP et la composition de calques restent à vérifier sur une VM
  Windows neuve.
- **ARM64 non pris en charge** : seule l'architecture x64 est publiée.
- **Licence du code Memo non définie** : « licence à définir avant diffusion ».
  Ne pas publier cette version comme produit distribué avant décision.
- **Pas de mise à jour automatique** : chaque version est téléchargée et
  installée manuellement.
- **Taille** : la publication complète pèse environ 770 Mo, l'installation
  environ 349 Mo et l'installateur environ 95 Mo (mesures du 30 octobre 2026).
  Prévoir un téléchargement et une installation longs.
- **VC++ Redistributable non embarqué** : les DLL LibVLC en dépendent sur une
  installation Windows nue.
- **Version du binaire** : `Memo.exe` reste en `FileVersion 1.0.0.0` tant que
  le projet ne fixe pas `Version`; l'installateur et ces notes portent 0.1.0.
- **Journal de plantage** : `memo-crash.log` est écrit dans le dossier
  d'installation, supprimé avec celui-ci.

## Installation

Prérequis : Windows 10 21H2 (build 19044) ou Windows 11, x64 ; prévoir 1 Go
libre. Aucun droit administrateur n'est nécessaire.

1. Télécharger `Memo-0.1.0-win-x64-setup.exe` depuis la publication officielle.
2. Vérifier l'empreinte SHA-256 :

   ```powershell
   Get-FileHash .\Memo-0.1.0-win-x64-setup.exe -Algorithm SHA256
   ```

   Empreinte de cette version :

   ```text
   SHA-256 : F9B0F88526F1A72E799935B640FDC84D0797FD7ACBCC8B1E1E5376A1311618CF
   ```

   Taille de l'installateur : 99 884 040 octets (95,3 Mio).

3. Lancer l'installateur. Il n'est pas signé : SmartScreen peut demander
   **Informations complémentaires > Exécuter quand même**.
4. Laisser le dossier proposé `%LOCALAPPDATA%\Programs\Memo` ou en choisir un
   autre dans le profil utilisateur.
5. Lancer Memo. La version 0.1.0 ouvre la bibliothèque de démonstration ; elle
   ne crée ni n'importe de bibliothèque personnelle.

La désinstallation passe par **Paramètres Windows > Applications >
Applications installées > Memo**. Les bibliothèques et sauvegardes créées hors
du dossier d'installation ne sont pas supprimées.

Documentation complète : [`docs/install.md`](docs/install.md). Procédure de
publication : [`docs/release.md`](docs/release.md).
