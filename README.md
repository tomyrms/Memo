# Memo

Application Windows pour conserver ses Souvenirs Snapchat sur son disque et les redécouvrir : galerie chronologique, lecture façon Story, « Ce jour-là », favoris, albums, récapitulatifs et recherche locale.

**État : version 0.1.0, application fonctionnelle en cours de validation de distribution.** Import multi-ZIP, catalogue local, lecture photo/vidéo avec calques, albums, recherche, sauvegarde/restauration et rappels sont implémentés. Les limites sont décrites plus bas, sans les masquer.

Dépôt : [tomyrms/Memo](https://github.com/tomyrms/Memo).

## Installation

Prérequis : Windows 10 21H2 (build 19041) ou Windows 11, x64.

1. Télécharger `MemoSetup-0.1.0.exe` depuis la release GitHub.
2. Lancer l'installateur. Il installe Memo dans `%LOCALAPPDATA%\Programs\Memo` sans élévation.
3. L'installateur n'est pas signé : SmartScreen peut afficher un avertissement. Vérifier le SHA-256 publié avec la release.

Les données restent locales. Aucun compte, aucun envoi de média, aucun service distant.

## Premier lancement

1. Ouvrir **Réglages → Bibliothèque → Créer**.
2. Choisir un nom et un dossier vide : Memo y crée `catalog.db`, `media/` et `derived/`.
3. Aller dans **Imports**, sélectionner un ou plusieurs ZIP (ou un dossier de ZIP), puis **Importer**.
4. Les archives suivantes complètent la bibliothèque ; les favoris, albums et dates corrigées sont conservés.

L'import reconnaît le format des exports qui contiennent déjà les médias :

```
memories/<UUID>-main.jpg
memories/<UUID>-main.mp4
memories/<UUID>-overlay.png   (PNG ou WebP, malgré l'extension)
```

Il ne suppose pas que tous les exports Snapchat utilisent ce format. Les index HTML/JSON à liens distants ne sont pas activés tant qu'un exemple réel n'a pas été validé ; aucun téléchargement réseau n'est effectué.

## Fonctionnalités

- **Import multi-ZIP** avec reprise, idempotence, espaces sources séparés, diagnostics par élément.
- **Dates explicites** : provenance de chaque date, correction manuelle, fuseau de classement recalculable, 29 février traité exactement.
- **Galerie** par année/mois, filtres photo/vidéo, vue « Date inconnue », éléments à compléter.
- **Lecteur Story** photo/vidéo, calques alignés, progression, clavier, son, apparence avec ou sans calque.
- **« Ce jour-là »**, « Une journée à redécouvrir » et « Surprends-moi ».
- **Albums, favoris, exclusions** persistés et conservés lors des réimports.
- **Recherche** texte (dont OCR local), visuelle par empreinte, groupes de visages facultatif.
- **Récapitulatifs** annuels, mensuels et saisonniers, modifiables et enregistrables.
- **Carte hors ligne** des souvenirs disposant d'un lieu explicite.
- **Sauvegarde/restauration** portable et export d'une sélection.
- **Export MP4** d'une Story via Media Foundation.
- **Rappels Windows** facultatifs, discrets, via une tâche planifiée visible dans le Planificateur de tâches.

## Données locales

```
<Bibliothèque>/
  catalog.db                 catalogue SQLite
  library.json               identité et fuseau de la bibliothèque
  media/<xx>/<sha256>.<ext>  octets sources, jamais réécrits
  derived/thumbs/            miniatures reconstructibles
  imports/                   provenance des lots
```

Les chemins internes sont relatifs : une bibliothèque peut être déplacée ou restaurée sur un autre PC.

## Développement

Prérequis : .NET SDK 10, Windows 10/11 x64. Pour l'installateur : Inno Setup 6.

```powershell
git clone https://github.com/tomyrms/Memo.git
cd Memo
dotnet build Memo.slnx -c Debug
dotnet test tests/Memo.Core.Tests/Memo.Core.Tests.csproj
dotnet test tests/Memo.Media.Tests/Memo.Media.Tests.csproj
dotnet test tests/Memo.Integration.Tests/Memo.Integration.Tests.csproj

# Corpus synthétique déterministe
dotnet run --project tools/Memo.Corpus -- --out "$env:TEMP\memo-corpus" --seed 42 --count 20

# Installateur
./scripts/build-installer.ps1
```

Architecture : `Memo.Core` (domaine, SQLite, import, dates, découverte, sauvegarde), `Memo.Media` (formats, EXIF, WebP, HEVC, miniatures, composition), `Memo.App` (WinUI 3, lecteur, analyses locales, rappels, export), `tools/Memo.Corpus` (fixtures synthétiques).

Dépendances principales : Windows App SDK 1.8, SkiaSharp 4.154, LibVLCSharp 3.10 / LibVLC 3.0.24 (LGPL, DLL séparées), Microsoft.Data.Sqlite 10.0. Voir `LICENSES/` et `docs/decisions/`.

## Limites connues

- Les index Snapchat à liens distants ne sont pas pris en charge dans cette version.
- L'OCR dépend d'un pack de langue Windows ; l'absence est signalée sans bloquer l'application.
- Les groupes de visages sont des **suggestions** (empreinte 64 bits), pas des identités certaines.
- Les vidéos HEVC sont lues via LibVLC ; l'export MP4 nécessite l'encodeur H.264 de Media Foundation (Windows N : Media Feature Pack).
- La validation sur une installation Windows propre sans extensions HEVC/WebP reste à effectuer.
- L'installateur n'est pas signé.
- Le rendu des calques semi-transparents à l'export peut différer légèrement de la source.

## Documentation

| Document | Contenu |
|---|---|
| [Spécification](docs/specification.md) | Parcours et règles produit |
| [Architecture](docs/architecture.md) | Modèle, import, orientation, validation |
| [Formats](docs/formats.md) | Format observé, variantes, contrat d'adaptateur |
| [Installation](docs/install.md) | Prérequis, premiers pas, dépannage |
| [Release](docs/release.md) | Procédure de publication |
| [QA](docs/qa-report.md) | Constats, risques et tests non exécutés |
| [Accessibilité](docs/accessibility.md) | Audit clavier, lecteur d’écran, contraste, échelle |
| [Progression](PROGRESS.md) | État réel, décisions, blocages |
| [Design](DESIGN.md) | Direction artistique et revues d'écran |

Projet indépendant, sans affiliation avec Snapchat ou Snap. L'application s'appuie sur les exports fournis par leur propriétaire. Licence du code : à définir avant diffusion sous licence.
