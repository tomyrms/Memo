# Progression — Memo

Dernière mise à jour : 30 octobre 2026.

## État

Application WinUI 3 fonctionnelle sur bibliothèque réelle : import multi-ZIP, catalogue SQLite, dates, galerie, lecteur Story photo/vidéo avec calques, albums, recherche texte/OCR/visuelle, carte hors ligne, récapitulatifs, sauvegarde/restauration, rappels Windows, analyse locale. Distribution en cours.

## Décisions prises

- WinUI 3 non empaqueté, auto-contenu, x64 (`docs/decisions/0001-socle-windows.md`).
- Vidéo : LibVLC en mode frames mémoire (`SetVideoFormatCallbacks`) rendu par SkiaSharp. Pas de `VideoView`, pas de fenêtre VLC séparée, pas de dépendance à l'extension HEVC Windows.
- Images : SkiaSharp (JPEG/PNG/WebP), EXIF appliqué une seule fois.
- Bibliothèque : `catalog.db` + `media/<sha>` + `derived/thumbs` + `library.json`.
- Formats : adaptateur `memories/<UUID>-main|overlay` activé ; index Snapchat à liens non activé (aucun exemple validé).

## Terminé

- 20 issues lues, dépôt et branches vérifiés, skills Impeccable/Uncodixfy installés et appliqués.
- `Memo.Core` : import multi-ZIP, reprise, idempotence, espaces, dates/fuseaux, découverte, albums, sauvegarde, analyses. **39 tests**.
- `Memo.Media` : formats réels, EXIF, WebP, HEVC, miniatures, composition, géométrie. **69 tests**.
- Corpus synthétique déterministe + documentation de format.
- UI : 11 écrans, design system, états vides, captures en 1320×860 et 1040×1800.
- Branchement réel : import, galerie, albums, favoris, exclusions, correction de date, lieux, recherche, réglages, sauvegarde/restauration.
- Lecteur Story embarqué (HEVC + calques), export MP4 via Media Foundation.
- OCR Windows local, empreintes dHash, groupes de visages (suggestions), recherche visuelle.
- Rappels : toast + tâche planifiée `schtasks`, mode discret, activation par notification.
- Installateur Inno Setup (agent en cours de validation).

## Vérifications exécutées

- `dotnet test` Core : 39/39 ; Media : 69/69.
- Import du corpus : 22 souvenirs espace A + 1 espace B, 21 médias, 18 calques, 2 diagnostics, 23 cartes.
- Analyse locale sur la bibliothèque : 13 OCR, 13 empreintes, 5,7 s.
- Sauvegarde → restauration : 23 souvenirs retrouvés.
- Lecture HEVC et photo avec calque vérifiées à l'écran (sortie embarquée).
- Captures : accueil, bibliothèque, albums, récapitulatifs, recherche, carte, imports, réglages, Story.

## En cours

- Génération et validation de l'installateur Windows.
- Validation de l'import et de l'export via l'interface (pas seulement par harnais).
- Mise à jour README, notes de version et issues GitHub.

## Prochaine action

Construire l'installateur, l'installer dans un dossier de test, vérifier lancement/désinstallation, puis publier la release 0.1.0 et fermer les issues satisfaites.

## Blocages

- Pas de machine Windows propre disponible : la validation sans extension HEVC/WebP restera documentée comme non vérifiée.
- Format d'index Snapchat avec liens absent : adaptateur distant laissé inactif.
- Notifications Windows désactivées au niveau utilisateur sur cette machine : le toast n'a pas pu être observé visuellement ; la tâche planifiée et les API ont été testées.
- Installateur non signé : SmartScreen affichera un avertissement.

## Journal

- 30/10 : audit de l'export, cadrage, prototype média, moteurs Core/Media, UI, branchement réel, analyses, rappels, export, installateur.
