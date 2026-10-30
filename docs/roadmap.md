# Feuille de route — Memo

## Objectif

Construire une application Windows qui rassemble les différentes archives d’un export Snapchat, conserve les médias localement et permet de les consulter dans une galerie, de les lire façon Story et de les redécouvrir avec « Ce jour-là ».

Cette feuille de route décrit le travail à réaliser. Elle ne signifie pas que l’application ou ses fonctionnalités sont déjà implémentées. Le backlog sérialisé de référence est [`planning/issues.json`](../planning/issues.json) ; ses identifiants `SD-001` à `SD-020` sont stables ; les numéros et liens GitHub réels figurent dans les tableaux ci-dessous.

## Règles communes

- **Raisonner sur les archives importées.** Une seule archive n’est pas nécessairement l’export complet. Conserver les médias, calques et métadonnées en attente, puis les rapprocher lorsque d’autres archives arrivent.
- **Préserver les sources.** Les fichiers importés restent immuables. Miniatures, rendus composés, corrections et analyses sont stockés séparément.
- **Conserver la provenance.** Une date de conteneur n’est pas automatiquement une date de prise de vue certaine. Les dates, lieux et catégories gardent leur source, leur précision et leur fiabilité.
- **Garder les données inconnues visibles.** Une photo sans date reste consultable. Un calque seul est un élément à compléter, et non la preuve d’un souvenir définitivement perdu.
- **Séparer les sources.** Un identifiant n’est interprété que dans son espace source. Ne pas fusionner des comptes ou supposer des identifiants stables entre exports sans preuve.
- **Valider les formats.** Les adaptateurs ne reposent que sur des structures examinées. Les fichiers HTML sont lus statiquement, sans exécution de scripts. Les formats inconnus produisent un diagnostic.
- **Limiter les données utilisées.** Les fichiers généraux du compte ne sont pas requis pour importer les Souvenirs. Les médias des conversations restent hors périmètre par défaut.
- **Protéger le dépôt.** N’y conserver aucun export, média, identifiant de compte ou URL signée personnels. Les exemples de validation sont synthétiques ou explicitement anonymisés.

## État des preuves

Les pièces de cadrage comprennent des rapports externes et un inventaire tabulaire portant sur **un seul ZIP**. Elles ne donnent pas accès aux médias de toutes les archives et ne prouvent pas la complétude d’un export.

L’inventaire permet de contrôler ses propres comptages et associations. Les signatures binaires, rendus, codecs et métadonnées internes décrits dans les rapports doivent encore être vérifiés sur des fichiers représentatifs. En particulier :

- Les rapports mentionnent des WebP portant une extension `.png` ; le nom ou la colonne `type` de l’inventaire ne vérifie pas ce contenu.
- Les dimensions de l’inventaire paraissent déjà exprimées dans le sens d’affichage. Le lecteur devra distinguer dimensions encodées et affichées afin d’éviter une double rotation.
- Les résultats de « Ce jour-là » changent selon le fuseau utilisé. Le jour local et la sémantique de la date doivent rester explicites.
- Un UUID v4 n’exprime pas un ordre chronologique. Des vidéos partageant un timestamp ne sont pas fusionnées automatiquement en une séquence.

## M1 — MVP : importer, consulter et retrouver ses souvenirs

Le premier jalon comprend le parcours complet : importer plusieurs archives, compléter les associations, consulter les médias disponibles, retrouver une date, lire une Story, utiliser « Ce jour-là » et conserver ses choix.

| ID | Travail | Priorité |
|---|---|---|
| [SD-001 / #1](https://github.com/tomyrms/Memo/issues/1) | Socle Windows et bibliothèque locale | P0 |
| [SD-002 / #2](https://github.com/tomyrms/Memo/issues/2) | Formats reconnus et corpus synthétique | P0 |
| [SD-003 / #3](https://github.com/tomyrms/Memo/issues/3) | Import de plusieurs ZIP, reprise et ajout progressif | P0 |
| [SD-004 / #4](https://github.com/tomyrms/Memo/issues/4) | Associations entre archives et réimports idempotents | P0 |
| [SD-005 / #5](https://github.com/tomyrms/Memo/issues/5) | Décodage, orientation et rendu des calques | P0 |
| [SD-006 / #6](https://github.com/tomyrms/Memo/issues/6) | Dates, provenance et fuseaux | P0 |
| [SD-007 / #7](https://github.com/tomyrms/Memo/issues/7) | Galerie et vues à compléter | P0 |
| [SD-008 / #8](https://github.com/tomyrms/Memo/issues/8) | Lecture façon Story | P0 |
| [SD-009 / #9](https://github.com/tomyrms/Memo/issues/9) | Ce jour-là sur les années disponibles | P0 |
| [SD-010 / #10](https://github.com/tomyrms/Memo/issues/10) | Téléchargement depuis des index de liens validés | P1 |
| [SD-011 / #11](https://github.com/tomyrms/Memo/issues/11) | Albums, favoris et exclusions | P1 |
| [SD-012 / #12](https://github.com/tomyrms/Memo/issues/12) | Sauvegarde et restauration portables | P1 |
| [SD-013 / #13](https://github.com/tomyrms/Memo/issues/13) | Distribution Windows et accessibilité | P0 |

### Ordre de réalisation

Les contrats de données et les formats ouvrent le travail sur l’import. Le rapprochement des fichiers permet ensuite de traiter en parallèle le décodage et les dates. La galerie et la lecture Story conduisent à « Ce jour-là », puis aux personnalisations et à la sauvegarde.

La distribution commence à être préparée dès le choix du socle, mais sa validation finale porte sur le parcours intégré. L’adaptateur de liens `SD-010` peut avancer en parallèle lorsqu’un exemple approprié est disponible. L’absence d’un exemple d’index validé ne bloque pas une version documentée comme prenant en charge les archives contenant déjà les médias.

### Critère de sortie

Une installation Windows propre peut ouvrir une bibliothèque locale, importer les parties disponibles d’un export pris en charge, retrouver les médias, lire une Story, consulter les Flashbacks selon une politique de dates explicite et restaurer une sauvegarde. Les éléments non datés ou non associés restent visibles et peuvent être enrichis par un nouvel import.

## M2 — Redécouverte : récapitulatifs, rappels et collections

| ID | Travail | Priorité |
|---|---|---|
| [SD-014 / #14](https://github.com/tomyrms/Memo/issues/14) | Récapitulatifs annuels, mensuels et saisonniers | P1 |
| [SD-015 / #15](https://github.com/tomyrms/Memo/issues/15) | Notifications Windows facultatives | P1 |
| [SD-016 / #16](https://github.com/tomyrms/Memo/issues/16) | Suggestions d’événements et lieux disponibles | P2 |
| [SD-017 / #17](https://github.com/tomyrms/Memo/issues/17) | Export d’une Story ou d’un récapitulatif en vidéo | P2 |
| [SD-020 / #20](https://github.com/tomyrms/Memo/issues/20) | Accueil À redécouvrir et Surprends-moi | P1 |

Le jalon comprend l’accueil « À redécouvrir » et « Surprends-moi », qui varie les périodes proposées et limite les répétitions. Les sélections proposées sont modifiables et respectent les exclusions. Les cartes utilisent des lieux directement liés aux souvenirs ou renseignés par l’utilisateur. Les rappels nécessitent une activation volontaire et décrivent leur fonctionnement lorsque la fenêtre est fermée.

## M3 — Avancé : recherche locale facultative

| ID | Travail | Priorité |
|---|---|---|
| [SD-018 / #18](https://github.com/tomyrms/Memo/issues/18) | OCR et recherche visuelle locale | P2 |
| [SD-019 / #19](https://github.com/tomyrms/Memo/issues/19) | Regroupement par personnes | P2 |

Ces fonctions produisent de nouvelles données dérivées ; elles ne recréent pas à l’identique des catégories Snapchat. Elles sont activables séparément, leurs résultats sont corrigeables et les données calculées peuvent être supprimées sans effacer les souvenirs.

## Décisions à formaliser

1. Pile Windows, catalogue, migrations, composants multimédias distribués et licences.
2. Définition et reconnaissance des espaces sources, ainsi que le traitement des associations ambiguës.
3. Politique des dates supposées dans « Ce jour-là », fuseau d’affichage et règle du 29 février.
4. Distinction entre mutualisation de fichiers identiques et maintien de souvenirs logiques distincts.
5. Règles de composition et d’orientation validées pour les médias et calques.
6. Fonctionnement des notifications quand l’application est fermée et comportement du fond de carte hors ligne.
7. Formats effectivement pris en charge dans chaque version, avec leurs limites documentées.

## Priorités et validation

`P0` désigne les fondations et fonctions centrales du premier parcours. `P1` complète ce parcours ou la redécouverte quotidienne. `P2` couvre les enrichissements suivants. Ces niveaux ne sont pas des estimations de durée.

Chaque issue contient ses dépendances, son périmètre, des critères cochables et une validation ciblée sur le risque réel. Les contrôles de rendu et de restauration utilisent des cas représentatifs ; aucun test de documentation ni validation massive sans objectif concret n'est demandé.

## État de livraison — 30 octobre 2026

| Jalon | État | Preuves |
|---|---|---|
| M1 — importer, consulter, retrouver | **réalisé, validation Windows propre à faire** | 39 tests Core, 69 tests Media, import UI de 6 ZIP, captures écran, sauvegarde/restauration 23 souvenirs |
| M2 — redécouverte | **réalisé avec limites** | accueil, Surprends-moi, récapitulatifs enregistrés, export MP4, rappels planifiés, carte hors ligne |
| M3 — recherche locale | **réalisé avec limites** | OCR Windows local, empreintes dHash, groupes de visages, recherche visuelle dans l'interface |
| Distribution | **en cours** | installateur Inno Setup, release 0.1.0, validation sur machine propre non disponible |

Les éléments « réalisé » ne dispensent pas des vérifications manuelles listées dans `docs/qa-report.md` et `docs/manual-tests.md`. Les limites (index distant, packs de langue, visages indicatifs, signature) restent ouvertes.
