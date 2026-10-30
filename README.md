# Memo

Application Windows pour conserver ses Souvenirs Snapchat sur son disque et les redécouvrir : galerie chronologique, lecture façon Story, « Ce jour-là », favoris, albums et récapitulatifs.

**État : projet cadré, développement à réaliser.** Ce dépôt prépare le produit et son backlog. Il ne contient pas encore d'application installable. Dépôt : [tomyrms/Memo](https://github.com/tomyrms/Memo).

## Expérience attendue

1. Sélectionner les ZIP d'un export Snapchat, ou un dossier déjà extrait.
2. Retrouver immédiatement les médias disponibles pendant que l'import se poursuit.
3. Ajouter d'autres archives plus tard : l'application complète les souvenirs déjà importés sans perdre les favoris ou les corrections.
4. Parcourir les années, les mois et les journées, ou ouvrir « Ce jour-là » en lecture Story.
5. Sauvegarder la bibliothèque sur un autre disque et la restaurer sur un autre PC.

La consultation et les fonctions de redécouverte fonctionnent hors ligne une fois les médias récupérés. L'import d'un export contenant des liens demande une connexion pour télécharger ces médias.

## Principes

- **Plusieurs ZIP dès le départ.** Un calque, son média et ses métadonnées peuvent arriver dans des archives différentes.
- **Fichiers sources conservés.** L'application garde les octets importés ; les miniatures, rendus composés et analyses sont des dérivés séparés.
- **Dates explicites.** Conserver la valeur brute, sa provenance, sa précision et les corrections. Ne pas transformer la date du ZIP en date du souvenir.
- **Bibliothèque utilisable sans index.** Les photos sans date restent visibles et peuvent être enrichies ultérieurement.
- **Apparence fidèle.** Associer média et calque, reconnaître le format réel et appliquer une seule fois l'orientation.
- **Données locales.** Pas de compte pour consulter sa bibliothèque, pas d'envoi des médias à un service d'analyse. Les fonctions d'analyse locale avancées sont facultatives.
- **Développement avec des exemples synthétiques.** Aucun export personnel, historique de compte, jeton de téléchargement ou inventaire nominatif dans le dépôt.

## Documentation

| Document | Contenu |
|---|---|
| [Spécification produit](docs/specification.md) | Parcours, fonctions et règles d'usage |
| [Architecture et import](docs/architecture.md) | Modèle, variantes d'export et choix à valider |
| [Analyse critique des fichiers fournis](docs/analyse-import.md) | Résultats recoupés, limites et corrections |
| [Feuille de route](docs/roadmap.md) | Jalons et ordre de développement |
| [Backlog structuré](planning/issues.json) | Issues prêtes à créer, critères et dépendances |
| [Publication sur GitHub](planning/README.md) | État de publication et règles de reprise |

## Première version

Import de plusieurs archives, association des médias/calques, lecture photo/vidéo, gestion des dates, galerie, lecture Story, « Ce jour-là », favoris et albums, sauvegarde/restauration et première distribution Windows.

Les récapitulatifs mensuels/annuels/saisonniers, rappels Windows, suggestions d'événements, carte, export vidéo, OCR, recherche visuelle et groupes de personnes sont prévus dans les jalons suivants. « Surprends-moi » fait partie de l'espace de redécouverte.

## Point de départ technique

Une application Windows native en C#/.NET avec WinUI 3 et un catalogue SQLite est la **piste de départ à valider** dans la première issue. Les versions, le lecteur vidéo, le décodage HEVC/WebP et les composants distribués doivent être décidés sur un prototype représentatif avant de figer l'architecture. Aucun support de codec ou de version de Windows n'est annoncé comme testé à ce stade.

Projet indépendant, sans affiliation avec Snapchat ou Snap. L'application s'appuie sur les exports fournis par leur propriétaire.
