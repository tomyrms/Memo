# Installation de Memo

Version concernée : **0.1.0**, Windows x64. Ce document décrit l'installateur
Inno Setup produit par `scripts/build-installer.ps1`. Il ne remplace pas les
notes de version : voir [`RELEASE.md`](../RELEASE.md) et
[`docs/release.md`](release.md).

> **État de la version 0.1.0.** L'installateur et l'application de démonstration
> sont construits, mais l'import réel et la création de bibliothèque ne sont pas
> encore reliés à l'interface. Les sections « Import » et « Emplacement des
> données » distinguent ce qui existe de ce qui est prévu. Ne présentez pas
> cette version comme une version d'import finalisée.

## Prérequis

| Élément | Exigence |
|---|---|
| Système | Windows 10 21H2 (build 19044) ou Windows 11 |
| Architecture | x64 uniquement ; ARM64 non pris en charge |
| Espace disque | Prévoir 1 Go libre (programme, DLL LibVLC et futures bibliothèques) |
| Droits | Aucun droit administrateur : installation dans le profil utilisateur |
| Runtime .NET | Inclus dans la publication auto-contenue ; aucune installation séparée |
| Runtime natif | Microsoft Visual C++ Redistributable 2015-2022 x64, généralement déjà présent ; requis par les DLL LibVLC |
| Réseau | Facultatif pour consulter une bibliothèque ; nécessaire seulement pour télécharger un export ou une future fonction en ligne |

L'installateur refuse les systèmes antérieurs à Windows 10 build 19041. Cette
borne technique ne vaut pas validation : la cible annoncée et à tester est
Windows 10 21H2 / Windows 11 x64.

## Télécharger et vérifier

Le fichier attendu est :

```text
Memo-0.1.0-win-x64-setup.exe
```

Il est publié avec une empreinte SHA-256, soit dans la note de version, soit
dans le fichier `Memo-0.1.0-win-x64-setup.exe.sha256`. Vérifiez-la avant de
lancer l'installateur :

```powershell
Get-FileHash .\Memo-0.1.0-win-x64-setup.exe -Algorithm SHA256
Get-Content .\Memo-0.1.0-win-x64-setup.exe.sha256
```

Les deux valeurs doivent être identiques. Si le fichier vient d'un site qui
n'est pas le dépôt officiel [tomyrms/Memo](https://github.com/tomyrms/Memo),
ne l'exécutez pas.

## Installation

1. Double-cliquez sur `Memo-0.1.0-win-x64-setup.exe`.
2. Windows SmartScreen peut afficher un avertissement : l'installateur n'est
   **pas signé**. Voir la section [SmartScreen](#smartscreen).
3. Lisez la page « Installateur non signé », puis poursuivez si l'empreinte
   correspond.
4. Conservez le dossier proposé, `%LOCALAPPDATA%\Programs\Memo`, ou choisissez
   un autre dossier de votre profil. L'installation ne demande pas
   d'élévation.
5. Le raccourci du menu Démarrer est créé. Le raccourci du bureau est une tâche
   facultative, décochée par défaut.
6. À la fin, lancez Memo ou décochez l'option.

L'installateur installe :

- `Memo.exe` et les fichiers .NET / Windows App SDK auto-contenus ;
- les DLL LibVLC dans `libvlc\win-x64` ;
- `Assets\`, notamment l'icône et les données de démonstration ;
- `LICENSES\` et `THIRD-PARTY-NOTICES.txt` ;
- un désinstalleur visible dans Paramètres Windows.

Les symboles de débogage (`.pdb`), les fichiers `.lib` et les binaires LibVLC
des architectures x86/ARM64 ne sont pas installés : ils ne servent pas à
l'exécution x64.

## Premier lancement

Au premier lancement, Memo ouvre une **bibliothèque de démonstration** avec des
médias synthétiques. Aucun compte n'est demandé et aucun média personnel n'est
créé ni envoyé sur le réseau.

L'écran Réglages affiche la version, le mode de données et le chemin de la
bibliothèque lorsqu'une bibliothèque réelle est ouverte. Dans la version 0.1.0,
la création et l'ouverture d'une bibliothèque affichent encore un message
« non disponible » : le noyau `Memo.Core` existe et est testé, mais la liaison
avec l'interface reste à faire.

En cas de plantage au démarrage, un journal est écrit dans le dossier
d'installation :

```text
%LOCALAPPDATA%\Programs\Memo\memo-crash.log
```

## Import d'un export

### Ce qui fonctionne dans 0.1.0

L'écran **Imports** permet de sélectionner plusieurs fichiers ZIP ou un dossier
déjà extrait et d'afficher la sélection. Le bouton d'import affiche toutefois
« Bibliothèque non ouverte » : l'écriture dans une bibliothèque n'est pas
encore reliée à l'interface. Aucun fichier utilisateur n'est modifié par cette
version.

### Parcours prévu

1. Ouvrir ou créer une bibliothèque depuis **Réglages** (fonction à relier).
2. Aller dans **Imports**, choisir l'espace source.
3. Sélectionner **tous les ZIP du même export**, dans n'importe quel ordre. Un
   export peut être réparti en plusieurs archives ; les médias, calques et
   métadonnées peuvent arriver séparément.
4. Ne pas dézipper et ne pas renommer les archives avant l'import. Les octets
   sources seront conservés.
5. Lancer l'import et laisser la bibliothèque se compléter. Un réimport de la
   même archive ne doit pas créer de doublon ni effacer favoris, albums ou dates
   corrigées.

Les médias des conversations (`chat_media/`) restent hors périmètre. Aucun
téléchargement distant n'est activé dans cette version.

## Emplacement des données

### Programme

```text
%LOCALAPPDATA%\Programs\Memo
```

Ce dossier contient uniquement les fichiers installés et le journal de
plantage éventuel. Il est supprimé par le désinstalleur, à l'exception des
fichiers que vous y auriez ajoutés vous-même.

### Bibliothèques

La version 0.1.0 **ne crée aucune bibliothèque** : l'emplacement sera choisi
explicitement par l'utilisateur lors de la création, et non imposé par
l'installateur. Une bibliothèque ne doit pas être placée dans le dossier
d'installation.

La structure définie par le noyau et documentée dans la
[décision 0001](decisions/0001-socle-windows.md) est :

```text
<Bibliothèque>\
  catalog.db
  library.json
  media\<sha256[0..2]>\<sha256>.<ext>
  derived\thumbs\<memoryId>.jpg
  derived\composite\<memoryId>.webp
  imports\<batchId>.json
  settings
```

Les chemins sont relatifs à la racine de la bibliothèque. Une sauvegarde doit
conserver les relations du catalogue, pas seulement les fichiers média.

### Ce que l'installateur ne touche pas

L'installateur et le désinstalleur ne créent, ne déplacent et ne suppriment
aucun dossier de bibliothèque, aucune sauvegarde et aucun réglage utilisateur
hors du dossier d'installation. Voir la section
[Désinstallation](#désinstallation).

## Désinstallation

1. Ouvrir **Paramètres Windows > Applications > Applications installées**.
2. Rechercher **Memo**, puis choisir **Désinstaller**.
3. Ou utiliser le raccourci de désinstallation du groupe **Memo** dans le menu
   Démarrer.

Le désinstalleur retire le programme, les raccourcis et les fichiers installés.
Il **ne supprime pas** les bibliothèques, les sauvegardes ni les réglages
utilisateurs : ceux-ci sont en dehors du dossier d'installation et ne sont pas
suivis par l'installateur.

Pour effacer définitivement vos souvenirs, supprimez vous-même le ou les
dossiers de bibliothèque que vous avez choisis, après avoir vérifié leur
contenu. Cette suppression est irréversible.

## Dépannage

### SmartScreen

L'installateur et `Memo.exe` ne sont pas signés numériquement. SmartScreen peut
donc afficher « Windows a protégé votre PC » ou « Éditeur inconnu ». Ce n'est
pas une preuve de corruption, mais ce n'est pas non plus une garantie.

Procédure :

1. Vérifiez l'empreinte SHA-256 avec la commande ci-dessus.
2. Si elle correspond, cliquez sur **Informations complémentaires**, puis
   **Exécuter quand même**.
3. Si elle ne correspond pas, ou si le fichier vient d'une source inconnue,
   supprimez-le et retéléchargez-le depuis la publication officielle.

Une signature de code Authenticode est le seul moyen de supprimer durablement
cet avertissement. Elle n'est pas mise en place dans cette version.

### HEVC et lecture vidéo

Memo embarque LibVLC pour lire le H.264 et le HEVC sans dépendre de l'extension
HEVC du Microsoft Store. Si une vidéo ne se lance pas ou reste noire :

- vérifiez que `libvlc\win-x64\libvlc.dll` et `libvlc\win-x64\libvlccore.dll`
  existent dans le dossier d'installation ;
- vérifiez que le Microsoft Visual C++ Redistributable 2015-2022 x64 est
  installé (les DLL LibVLC en dépendent) ;
- vérifiez que l'antivirus n'a pas mis en quarantaine les DLL LibVLC ;
- réinstallez Memo si les fichiers manquent ;
- si seul un fichier précis échoue, le conteneur ou le codec peut être
  anormal ; un fichier source illisible doit rester conservé, pas supprimé.

La lecture HEVC a été vérifiée sur la machine de développement, mais pas encore
sur une installation Windows propre sans extension HEVC. Ce point reste une
limite connue.

### WebP et miniatures

SkiaSharp décode le WebP sans dépendre d'une extension Windows. Une image
d'extension `.png` peut en réalité contenir du WebP : Memo détecte le format par
signature, pas par extension. En cas de miniature absente :

- vérifiez la présence de `libSkiaSharp.dll` dans le dossier d'installation ;
- vérifiez que le fichier source n'est pas tronqué ou corrompu ;
- ne renommez pas le fichier pour « corriger » l'extension : cela ne change pas
  son contenu ;
- réinstallez Memo si la DLL SkiaSharp manque.

Le décodage WebP est couvert par les tests de `Memo.Media`, mais la composition
de calques réels reste à valider visuellement sur un corpus représentatif.

### L'application ne démarre pas

- Vérifiez que Windows est en x64 et au moins en build 19041.
- Vérifiez que le dossier d'installation n'a pas été déplacé après
  l'installation : les raccourcis pointent vers l'emplacement d'origine.
- Consultez `memo-crash.log` dans le dossier d'installation.
- Réinstallez dans le dossier proposé par défaut pour écarter un problème de
  droits ou de chemin.
- Un chemin contenant des caractères inhabituels peut gêner les composants
  natifs ; préférez `%LOCALAPPDATA%\Programs\Memo`.

### Fichier d'installation bloqué ou supprimé par l'antivirus

Un exécutable non signé peut être signalé à tort. Vérifiez l'empreinte, puis
restaurez le fichier depuis la quarantaine si vous faites confiance à la
source. Ne désactivez pas l'antivirus de façon permanente.

## Licences et composants tiers

Les textes sont installés dans `LICENSES\` et copiés à la racine de
l'installation dans `THIRD-PARTY-NOTICES.txt`. LibVLC et LibVLCSharp sont sous
LGPL-2.1-or-later ; leurs DLL restent séparées et remplaçables. Le code Memo
n'a pas encore de licence de diffusion : **licence à définir avant diffusion**.
