# Progression - Memo

Dernière mise à jour : 30 octobre 2026.

## État

Application WinUI 3 fonctionnelle sur bibliothèque réelle : import multi-ZIP et dossier complet, catalogue SQLite, dates, galerie, lecteur Story photo/vidéo avec calques, albums éditables, recherche texte/OCR/visuelle, carte hors ligne, récapitulatifs, sauvegarde/restauration vérifiée, rappels Windows, analyse locale incrémentale. Distribution Windows 0.1.0 reconstruite avec l'ensemble des correctifs.

## Décisions prises

- WinUI 3 non empaqueté, auto-contenu, x64 (`docs/decisions/0001-socle-windows.md`).
- Vidéo : LibVLC en mode frames mémoire (`SetVideoFormatCallbacks`) rendu par SkiaSharp. Pas de `VideoView`, pas de fenêtre VLC séparée, pas de dépendance à l'extension HEVC Windows.
- Images : SkiaSharp (JPEG/PNG/WebP), EXIF appliqué une seule fois.
- Bibliothèque : `catalog.db` + `media/<sha>` + `derived/thumbs` + `library.json`.
- Catalogue et manifeste versionnés par migrations idempotentes ; une version plus récente est refusée.
- Formats : adaptateur `memories/<UUID>-main|overlay` activé ; index Snapchat à liens non activé (aucun exemple validé).

## Terminé

- `Memo.Core` : import multi-ZIP, reprise, idempotence, espaces, dates/fuseaux, découverte, albums, sauvegarde, migrations, verrou d'opérations, analyses. **61 tests**.
- `Memo.Media` : formats réels, EXIF, WebP, HEVC, miniatures, composition, géométrie. **69 tests**.
- `Memo.Integration.Tests` : import de bout en bout. **12 tests**.
- Corpus synthétique déterministe + documentation de format.
- UI : 11 écrans, design system, états vides/chargement/erreur/désactivé, captures en 1320×860 et 1040×1800.
- Import : sélection de plusieurs ZIP ou d'un dossier, contrôle de l'espace disque, reprise, diagnostic des chemins disparus. Le disque de la bibliothèque se choisit depuis l'écran Imports, avec conservation de la sélection et reprise d'une bibliothèque Memo déjà présente.
- Premier lancement : création/ouverture de bibliothèque réelle, restauration possible sans bibliothèque ouverte.
- Lecteur Story : volume 0–100, muet synchronisé, heure exacte quand la précision le permet, enregistrement du média source sans réencodage, export de Story.
- Recherche : mêmes champs dans Bibliothèque et Recherche (lieu, date, OCR, identifiant), accents/casse, debounce et annulation, ordre de similarité visuelle conservé.
- Analyse OCR/vision incrémentale par défaut, rescan complet explicite.
- Albums : ajout, retrait, réordonnancement, lecture dans l'ordre, sources intactes.
- Sauvegarde/restauration : progression, annulation, fichier temporaire, vérification SHA-256 des médias, régénération des miniatures.
- Architecture : migrations versionnées, verrou des opérations longues, notification ouvrant la bonne bibliothèque par identifiant.
- Installateur Inno Setup par utilisateur, non signé.

## Vérifications exécutées

- `dotnet test Memo.slnx -c Release` : Core 61/61, Media 69/69, Intégration 12/12.
- Build Release : 0 erreur.
- Import réel : 838 souvenirs (22 synthétiques + 816 du premier ZIP), 477 sans date et 287 calques en attente des autres parties.
- Import des 17 ZIP réels relancé sur `X:` : fenêtre réactive pendant l'inventaire et la copie, progression par archive, plusieurs gigaoctets copiés sans gel de l'interface.
- Application lancée sur la bibliothèque réelle, accueil et lecteur vérifiés à l'écran.
- Analyse locale sur la bibliothèque : 13 OCR, 13 empreintes, 5,7 s.
- Sauvegarde → restauration : 23 souvenirs retrouvés ; hash modifié et média supprimé rejetés.
- Lecture HEVC et photo avec calque vérifiées à l'écran (sortie embarquée).
- Captures : accueil, bibliothèque, albums, récapitulatifs, recherche, carte, imports, réglages, Story.

## Limites connues

- Pas de machine Windows propre disponible : la validation sans extension HEVC/WebP reste documentée comme non vérifiée.
- Format d'index Snapchat avec liens absent : adaptateur distant laissé inactif (#10).
- Notifications Windows désactivées au niveau utilisateur sur cette machine : le toast n'a pas pu être observé visuellement ; la tâche planifiée et les API ont été testées.
- Installateur non signé : SmartScreen affichera un avertissement.
- Les 16 autres parties de l'export réel ne sont pas encore importées ; la bibliothèque de test reste partielle.
- Coaching de performance au-delà de 15 000–20 000 souvenirs documenté dans `docs/performance.md`.

## Prochaine action

Importer le dossier complet des 17 ZIP sur `X:`, vérifier l'association des calques et des dates, puis publier les notes de version définitives.

## Journal

- 30/10 : audit de l'export, cadrage, prototype média, moteurs Core/Media, UI, branchement réel, analyses, rappels, export, installateur, passe de correction complète (#29–#50), reconstruction de la release.
