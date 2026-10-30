# Progression — Memo

Dernière mise à jour : 30 octobre 2026.

## État

Socle validé : WinUI 3 + .NET 10 + SQLite + SkiaSharp + LibVLC. Le prototype de lecture HEVC fonctionne. Contrats du domaine écrits. Aucune fonctionnalité métier n'est encore implémentée.

## Décisions prises

- WinUI 3 non empaqueté, auto-contenu, x64 (voir `docs/decisions/0001-socle-windows.md`).
- LibVLC retenu pour la vidéo (HEVC sans extension préinstallée), SkiaSharp pour les images/WebP.
- Bibliothèque sur disque : `catalog.db`, `media/` content-addressed, `derived/` reconstructible.
- Contrats publics figés dans `src/Memo.Core/Domain.cs` et `Abstractions.cs`.

## Terminé

- Lecture du dépôt, des 20 issues et des documents de cadrage.
- Vérification des skills : Impeccable 0.1.7 (CLI) + skills 4.8.0 installées dans `~/.github/skills/impeccable` ; Uncodixfy cloné, révision `e0e0280`.
- `PRODUCT.md`, `DESIGN.md`, décision d'architecture, solution et projets.
- Prototype WinUI 3 + LibVLC : lecture HEVC vérifiée à l'écran.

## En cours

- Implémentation de `Memo.Core` (catalogue, import, dates, découverte, sauvegarde).
- Implémentation de `Memo.Media` (signatures, MP4, Skia, miniatures).
- Corpus synthétique et support de test.
- Coquille UI et système de composants.

## Prochaine action

Intégrer Core + Media, brancher l'import sur l'interface, puis construire la bibliothèque et le lecteur Story.

## Blocages

- Aucun export réel ne doit entrer dans le dépôt. Les tests utilisent le corpus synthétique.
- Validation sur une installation Windows propre (sans HEVC/WebP) non disponible pour l'instant.
- Format d'index Snapchat avec liens non fourni : adaptateur distant laissé inactif.

## Journal

- 30/10 : audit de l'export fourni (hors dépôt), cadrage, choix du socle, prototype média.
