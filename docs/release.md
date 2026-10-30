# Publication d'une version de Memo

Ce document décrit la procédure de build et de publication Windows. Il couvre
la version **0.1.0** et l'installateur Inno Setup produit par
[`scripts/build-installer.ps1`](../scripts/build-installer.ps1). Pour
l'installation côté utilisateur, voir [`docs/install.md`](install.md).

## Principes

- **Aucune signature de code** n'est configurée. L'installateur et `Memo.exe`
  restent non signés ; SmartScreen peut alerter. Ne pas présenter le fichier
  comme signé.
- **Aucun secret** dans le dépôt, dans les scripts ou dans les artefacts :
  pas de certificat, pas de mot de passe, pas de jeton GitHub.
- **Aucune donnée personnelle** dans le paquet, les journaux de build ou les
  notes de version. Les exemples sont synthétiques.
- **Pas de publication automatique** : la création de la release GitHub reste
  une action manuelle et relue.

## Prérequis de build

| Outil | Version / emplacement |
|---|---|
| Windows | Windows 10 21H2 ou Windows 11, x64 |
| .NET SDK | 10.0.x (`dotnet --version`) |
| Inno Setup | 6.x, `ISCC.exe` détecté par le script |
| PowerShell | 5.1 ou PowerShell 7 |
| Git | dépôt propre, branche de release |

Installer Inno Setup si nécessaire :

```powershell
pwsh -File scripts\install-inno.ps1
```

Le script demande confirmation, utilise winget (`JRSoftware.InnoSetup`) et
n'effectue aucun téléchargement direct. L'alternative manuelle est documentée
dans le script et sur <https://jrsoftware.org/isdl.php>.

## 1. Préparer la version

1. Vérifier que la branche est à jour et que le dépôt est propre :

   ```powershell
   git status --short
   git pull --ff-only
   ```

2. Mettre à jour la version dans `installer/Memo.iss` :

   ```ini
   #define MyAppVersion "0.1.0"
   VersionInfoVersion=0.1.0.0
   VersionInfoProductVersion=0.1.0
   ```

   `OutputBaseFilename` et le nom du fichier produit en découlent.

3. Rédiger ou mettre à jour `RELEASE.md` à partir du modèle fourni. Ne cocher
   comme terminé que ce qui a été exécuté et vérifié.

4. Vérifier la version affichée par l'application. Dans l'état actuel du dépôt,
   `src/Memo.App/Pages/SettingsPage.xaml.cs` contient encore `0.1.0` en dur et
   le projet ne fixe ni `Version` ni `AssemblyVersion` : `Memo.exe` porte donc
   `FileVersion 1.0.0.0` et une `ProductVersion` construite à partir du commit.
   Toute modification de `src/` doit être faite dans une tâche distincte et
   relue ; sinon, signaler l'écart dans les notes de version. L'installateur et
   les notes de version font foi pour le numéro 0.1.0.

5. Vérifier que les notes de licence sont à jour : `LICENSES/` et
   `THIRD-PARTY-NOTICES.txt`. Le code Memo n'a pas encore de licence de
   diffusion : la mention « licence à définir avant diffusion » doit rester
   présente. **Ne pas publier publiquement tant que cette décision n'est pas
   prise.**

## 2. Restaurer, compiler et tester

Depuis la racine du dépôt :

```powershell
dotnet restore Memo.slnx
dotnet build Memo.slnx -c Release --no-restore
dotnet test tests\Memo.Core.Tests\Memo.Core.Tests.csproj -c Release --no-build
dotnet test tests\Memo.Media.Tests\Memo.Media.Tests.csproj -c Release --no-build
```

Critère : compilation réussie et **0 test en échec** dans les deux projets. Les
avertissements existants doivent être examinés, pas masqués par la procédure de
release.

## 3. Publier l'application

La commande de référence est celle du script :

```powershell
dotnet publish src\Memo.App\Memo.App.csproj -c Release -r win-x64 `
  --self-contained -p:PublishReadyToRun=false -o artifacts\publish
```

`PublishReadyToRun=false` évite une étape de compilation anticipée longue et
des artefacts dépendants de la machine de build. La publication est
auto-contenue : le runtime .NET et Windows App SDK sont inclus.

Vérifier au minimum :

```powershell
Test-Path artifacts\publish\Memo.exe
Test-Path artifacts\publish\Memo.pri
(Get-ChildItem artifacts\publish -Filter *.xbf -Recurse).Count -gt 0
Test-Path artifacts\publish\libvlc\win-x64\libvlc.dll
Test-Path artifacts\publish\Assets\Memo.ico
```

`dotnet publish` n'inclut pas toujours les fichiers XAML compilés (`.xbf`) ni
`Memo.pri` pour une application WinUI 3 non empaquetée. Le script copie ces
artefacts depuis la sortie de build Release ; sans eux, l'application installée
meurt au démarrage en `STATUS_STOWED_EXCEPTION`.

La publication complète contient aussi les symboles `.pdb` et les binaires
LibVLC x86/ARM64. L'installateur les exclut ; l'artefact CI `memo-win-x64` est
allégé de la même façon.

## 4. Générer l'installateur

```powershell
pwsh -File scripts\build-installer.ps1
```

Le script :

1. vérifie la présence de `ISCC.exe` avant de publier ;
2. publie dans `artifacts\publish` ;
3. compile `installer\Memo.iss` avec sortie dans `artifacts\` ;
4. vérifie la présence de `Memo.exe` et de l'installateur ;
5. affiche la taille et le SHA-256 ;
6. écrit `Memo-0.1.0-win-x64-setup.exe.sha256`.

Sortie attendue :

```text
artifacts\publish\
artifacts\Memo-0.1.0-win-x64-setup.exe
artifacts\Memo-0.1.0-win-x64-setup.exe.sha256
```

Si Inno Setup est absent, le script s'arrête avec un message et ne télécharge
rien. Utiliser `scripts\install-inno.ps1` ou l'installation manuelle.

Pour recompiler sans republier, après avoir téléchargé l'artefact de build :

```powershell
pwsh -File scripts\build-installer.ps1 -SkipPublish
```

## 5. Vérifier l'empreinte SHA-256

```powershell
$file = 'artifacts\Memo-0.1.0-win-x64-setup.exe'
$expected = (Get-Content "$file.sha256").Split()[0]
$actual = (Get-FileHash $file -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "SHA-256 incohérent" }
"$actual  $([IO.Path]::GetFileName($file))"
```

Le fichier `.sha256` doit être joint à la release. La note de version peut
reprendre l'empreinte, mais le fichier sidecar reste la référence.

## 6. Tester l'installateur

À faire sur une machine ou une VM **Windows propre**, si possible sans SDK
.NET, sans extension HEVC et sans dossier Memo existant :

1. vérifier que l'installation ne demande pas d'élévation ;
2. vérifier le dossier `%LOCALAPPDATA%\Programs\Memo` ;
3. vérifier le raccourci du menu Démarrer et l'absence du raccourci bureau par
   défaut ;
4. lancer Memo, vérifier l'ouverture de la bibliothèque de démonstration ;
5. vérifier la présence de `LICENSES\LGPL-2.1.txt`, `LICENSES\MIT.txt` et de
   `THIRD-PARTY-NOTICES.txt` ;
6. créer un dossier de bibliothèque factice hors du dossier d'installation,
   désinstaller, vérifier que le programme disparaît et que le dossier factice
   reste ;
7. noter le comportement SmartScreen et la version affichée.

Cette validation sur machine propre **n'est pas encore réalisée** pour 0.1.0.
Un test silencieux local a vérifié l'installation dans un dossier temporaire,
la présence de `Memo.exe`, des DLL `libvlc\win-x64`, des licences, puis la
désinstallation et la conservation d'un dossier de données factice. Ce test ne
remplace pas une VM Windows neuve. Tant que la validation propre n'est pas
faite, la release doit rester une pré-version ou être présentée comme non
validée pour un usage quotidien.

## 7. Publier sur GitHub

Créer un tag annoté et une release manuelle :

```powershell
git tag -a v0.1.0 -m "Memo 0.1.0"
git push origin v0.1.0
gh release create v0.1.0 `
  artifacts\Memo-0.1.0-win-x64-setup.exe `
  artifacts\Memo-0.1.0-win-x64-setup.exe.sha256 `
  --title "Memo 0.1.0" `
  --notes-file RELEASE.md `
  --prerelease
```

- `--prerelease` est approprié tant que la validation sur machine propre et la
  licence ne sont pas réglées.
- Ne pas joindre `artifacts\publish` en entier : l'installateur est le paquet
  destiné aux utilisateurs.
- Ne pas joindre de fichiers de symboles, de journaux de build ou de données
  locales.
- Vérifier après publication que les assets sont téléchargeables et que
  l'empreinte correspond.

## 8. Artefacts attendus

| Artefact | Emplacement | Contenu |
|---|---|---|
| Publication | `artifacts\publish\` | Application auto-contenue win-x64 |
| Installateur | `artifacts\Memo-0.1.0-win-x64-setup.exe` | Paquet Inno Setup non signé |
| Empreinte | `artifacts\Memo-0.1.0-win-x64-setup.exe.sha256` | SHA-256 du fichier |
| Artefact CI build | `memo-win-x64` | Publication allégée (sans `.pdb`, `.lib`, LibVLC x86/ARM64) |
| Artefact CI installateur | `memo-installer-win-x64` | Exécutable et empreinte |

L'installateur inclut `LICENSES\` et `THIRD-PARTY-NOTICES.txt`, l'icône
`src/Memo.App/Assets/Memo.ico`, les DLL LibVLC séparées et remplaçables, et un
désinstalleur. Il n'inclut ni `.pdb`, ni `.lib`, ni les binaires LibVLC des
autres architectures.

## CI

Le workflow [`.github/workflows/build.yml`](../.github/workflows/build.yml)
s'exécute sur `windows-latest` :

- checkout, .NET 10, restore, build de `Memo.slnx` ;
- tests de `Memo.Core` et `Memo.Media` ;
- publication win-x64 et téléversement de `memo-win-x64` ;
- job `installer` sur les pushes et les lancements manuels (pas sur les pull
  requests) : installation de `innosetup` via Chocolatey, puis compilation avec
  `scripts\build-installer.ps1 -SkipPublish` si `ISCC.exe` est disponible.

Le job `installer` est **conditionnel** : si Chocolatey ou `ISCC.exe` n'est pas
disponible, il consigne un avertissement et n'échoue pas. Le workflow ne
contient ni secret, ni certificat, ni étape de signature.

## Revenir en arrière

- Supprimer ou dépublier la release GitHub concernée.
- Supprimer le tag local et distant si nécessaire :

  ```powershell
  git tag -d v0.1.0
  git push origin :refs/tags/v0.1.0
  ```

- Ne pas réutiliser un numéro de version déjà publié pour un contenu différent.
  Incrémenter la version et régénérer l'installateur.

## Limites et blocages connus

1. **Licence du code Memo non définie** : ne pas diffuser publiquement avant
   décision.
2. **Installateur non signé** : SmartScreen alertera ; aucune signature ne doit
   être simulée.
3. **Import et création de bibliothèque non reliés** dans l'interface 0.1.0 :
   ne pas annoncer l'import comme terminé.
4. **Validation sur machine Windows propre non réalisée** : HEVC, WebP,
   installation sans SDK et désinstallation restent à vérifier. Le test local
   silencieux ne couvre pas une VM neuve.
5. **ARM64 non pris en charge**.
6. **Tailles** : la publication complète pèse environ 770 Mo, l'installation
   environ 349 Mo et l'installateur environ 95 Mo (mesures du 30 octobre 2026).
   L'installateur compresse et exclut les fichiers inutiles ; prévoir le temps
   de compilation et de téléchargement.
7. **VC++ Redistributable** : non embarqué ; les DLL LibVLC en dépendent sur
   une installation Windows nue.
8. **Versions dans le binaire** : `Memo.exe` reste en `FileVersion 1.0.0.0`
   tant que le projet ne fixe pas `Version`; l'installateur et les notes
   portent 0.1.0.
