# Produit — Memo

## Ce qu'est Memo

Memo est une application Windows locale qui transforme les archives d'un export Snapchat en une bibliothèque de souvenirs consultable hors ligne. Elle importe plusieurs ZIP, rapproche les médias de leurs calques, conserve les fichiers sources, explique les dates disponibles et propose des parcours de redécouverte.

## Utilisateur et contexte d'usage

Une personne qui possède un export Snapchat, souvent réparti en plusieurs archives, et qui veut :

- retrouver ses photos et vidéos sur son PC, sans dépendre du service ;
- les parcourir par année, mois et jour, et revoir les souvenirs d'un même jour ;
- organiser quelques albums et garder des favoris ;
- sauvegarder l'ensemble et le restaurer sur un autre PC.

Usage principal : le soir, sur un écran de bureau ou un portable, au clavier et à la souris. La bibliothèque peut être volumineuse ; l'interface doit rester rapide et lisible.

## Promesse

**Les images d'abord, le cadre ensuite.** Memo ne fabrique pas de contenu : elle restitue fidèlement ce qui a été importé, indique ce qui manque et laisse l'utilisateur corriger.

## Principes produit

1. **Plusieurs archives dès le départ.** Un média, son calque et ses métadonnées peuvent arriver séparément. Un élément absent n'est jamais déclaré perdu.
2. **Sources immuables.** Les octets importés ne sont jamais réécrits. Miniatures, composites et analyses sont des dérivés supprimables.
3. **Dates explicites.** La date d'un conteneur n'est pas automatiquement une prise de vue. Chaque date garde sa source ; une correction manuelle prime sans effacer l'original.
4. **Espaces sources séparés.** Deux comptes ou exports distincts ne sont pas fusionnés sur un identifiant qui se ressemble.
5. **Hors ligne.** Consulter, rechercher et lire ne nécessitent aucun réseau. Les fonctions d'analyse sont locales et facultatives.
6. **Honnêteté des états.** Un calque seul est « média non trouvé », pas une photo. Une carte vide n'est pas une carte du monde. Une couverture partielle est annoncée.

## Surfaces

| Surface | Mode | Tâche principale |
|---|---|---|
| Accueil « À redécouvrir » | Operate / Experience | Voir ce qui mérite d'être rouvert aujourd'hui |
| Bibliothèque | Operate | Retrouver un média par date ou par filtre |
| Lecteur Story | Experience | Revivre une journée ou un album à la suite |
| Albums | Operate | Organiser sans déplacer les sources |
| Récapitulatifs | Operate | Composer et conserver une sélection |
| Événements et carte | Operate | Situer et regrouper dans le temps |
| Recherche | Operate | Trouver par texte, personne ou similitude |
| Imports | Operate | Ajouter des archives et comprendre le bilan |
| Réglages | Operate | Fuseau, sauvegarde, rappels, analyses |

## Critères de réussite

- Importer plusieurs ZIP dans un ordre quelconque donne le même résultat.
- Un réimport ne crée pas de doublon et conserve albums, favoris, exclusions et dates corrigées.
- Une photo sans date reste visible ; un calque seul reste identifiable.
- « Ce jour-là » suit le fuseau choisi, ignore les dates inconnues et traite le 29 février explicitement.
- Une sauvegarde restaurée sur une autre installation retrouve les relations, pas seulement les fichiers.
- L'application s'installe et se désinstalle proprement sur Windows 10/11 x64.

## Hors périmètre

- Compte en ligne, synchronisation, partage social.
- Import automatique des conversations (`chat_media/`).
- Reconnaissance faciale certaine ou nommage automatique depuis la liste d'amis.
- Promesse d'un « original » photographique non vérifié.

## Langue

Interface en français, libellés courts et concrets. Les messages d'erreur nomment le problème et la sortie.
