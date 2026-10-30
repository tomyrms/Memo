# Direction artistique — Memo

Version 1 — 30 octobre 2026. Document vivant : les revues d'écran y ajoutent leurs corrections.

## Intention

**Le cadre s'efface, la photo reste.** Memo est une visionneuse de souvenirs, pas un tableau de bord. La surface est chaude et neutre, les photos occupent la place, les commandes sont discrètes et prévisibles. Aucune décoration ne concurrence une image.

Référence de registre : un album photo posé sur une table en bois clair, pas une application « premium » sombre.

## Palette

Thème clair par défaut ; thème sombre natif.

| Rôle | Clair | Sombre |
|---|---|---|
| Fond | `#F4F1EC` | `#141210` |
| Surface | `#FCFAF6` | `#1C1917` |
| Surface secondaire | `#EAE5DC` | `#262220` |
| Bordure | `#D8D1C5` | `#37312B` |
| Texte | `#1D1A16` | `#F4EFE7` |
| Texte secondaire | `#5F594F` | `#B0A79A` |
| Accent | `#A64B1E` | `#E39A5C` |
| Sur accent | `#FFFFFF` | `#241407` |
| Accent doux | `#F2E0D1` | `#3A2A1C` |
| Danger | `#A33A2A` | `#E07A67` |
| Succès | `#2F6B4F` | `#7FB79B` |

Règles :

- L'accent signale une action ou une sélection, jamais une décoration.
- Pas de dégradé, pas de verre, pas de halo, pas d'ombre colorée.
- La photo est toujours posée sur `#0E0D0C` en plein écran.
- Les textes secondaires ne sont jamais gris pur : ils sont teintés du fond.

## Typographie

- Titres : **Segoe UI Variable Display**, SemiBold.
- Texte : **Segoe UI Variable Text**, Regular/SemiBold.
- Échelle : 28 (titre de page), 20 (section), 16 (sous-section), 14 (corps), 13 (secondaire), 12 (légende).
- Largeur de lecture 65–75 caractères ; pas de texte centré sur plusieurs lignes.
- Chiffres alignés dans les tableaux et compteurs.

Exception assumée à Uncodixfy : la police système Windows est conservée. Elle est native, gratuite, lisible et cohérente avec l'OS ; la remplacer par une police téléchargée serait une affectation.

## Espacement et géométrie

- Échelle : 4 / 8 / 12 / 16 / 24 / 32 / 48.
- Marge de page : 32 px horizontal, 24 px haut.
- Rayons : 4 px (contrôles), 6 px (cartes), 8 px (dialogues). Jamais de pilule décorative.
- Bordures : 1 px. Pas de bordure colorée latérale.
- Ombres : aucune sur les cartes ; 0 2px 8px rgba(0,0,0,.12) réservé aux flyouts et dialogues.
- Grille de bibliothèque : colonnes de 160–220 px, gouttière 8 px, ratio 3:4 ou natif selon le média.

## Composants

- **Bouton primaire** : fond accent, texte sur accent, rayon 4, hauteur 32.
- **Bouton secondaire** : surface, bordure 1 px, même géométrie.
- **Bouton discret** : transparent, visible au survol/focus.
- **Carte** : surface, bordure 1 px, rayon 6, padding 16. Jamais de carte dans une carte.
- **Tuile média** : rayon 6, image en `UniformToFill`, liseré de sélection 2 px accent, badge durée en bas à droite sur fond `#B3000000`.
- **Navigation** : `NavigationView` native à gauche, 240 px ouverte / 48 px compacte, sélection par fond `AccentSoft`, icône Fluent.
- **En-tête de page** : titre + actions à droite, pas de surtitre.
- **État vide** : titre, une phrase, une action réelle. Pas d'illustration décorative.
- **Progression** : barre fine en haut de la zone concernée + texte factuel (« 128 / 531 fichiers »).
- **Dialogue** : largeur 420–560, titre, corps, actions alignées à droite, bouton destructif à gauche.
- **Notification** : bandeau `InfoBar` en haut de page, refermable.

## États

- Vide, chargement (squelette ou texte), erreur, sélection, désactivé, date inconnue, média non retrouvé.
- Un état n'est jamais simulé par des données de démonstration hors mode démo explicite.
- Focus clavier : visuel système conservé, renforcé par un contour accent de 2 px sur les tuiles.
- Réduction des animations : respecter `UISettings.AnimationsEnabled` ; transitions réduites à un fondu de 100 ms.

## Mouvement

- 120–180 ms, ease-out, sur opacité et translation de 8 px maximum.
- Une animation explique un changement (ouverture du lecteur, ajout à un album) ; pas d'entrée systématique de chaque section.
- Aucun rebond, aucune rotation, aucun parallaxe.

## Iconographie

- **Segoe Fluent Icons**, 16 px (texte) et 20 px (navigation), trait cohérent.
- Pas d'emoji dans l'interface.
- Les icônes ne remplacent jamais un libellé seul dans une action destructrice.

## Adaptation Windows

- Respecter les conventions WinUI : `NavigationView`, `ContentDialog`, `InfoBar`, `TeachingTip`, menus contextuels, glisser-déposer.
- Zones cliquables ≥ 32 px ; cibles de tuiles ≥ 44 px.
- Le redimensionnement est testé à 1024, 1280 et 1600 px de large ; la grille se reflow, la barre latérale passe en compact.
- Les raccourcis clavier utilisent les conventions Windows : Espace (lecture/pause), flèches (navigation), Échap (retour), Ctrl+F (recherche), Suppr (retirer d'un album avec confirmation).
- Le thème suit Windows par défaut ; le choix explicite est mémorisé.

## Exceptions aux règles web d'Impeccable / Uncodixfy

1. **Police système** conservée (Segoe UI Variable) : règle native, pas un choix par défaut.
2. **Visuels de focus système** conservés et renforcés, jamais supprimés.
3. **Densité** adaptée au bureau Windows (32 px de contrôle) et non aux 44 px tactiles web.
4. Le détecteur Impeccable ne parse pas XAML : les contrôles sont revus par inspection du code et captures réelles.

## Revues

Méthode : revue de design native (Impeccable 4.8.0, `audit.native` / `craft-floor`), sans détecteur automatique — celui-ci ne lit pas XAML. Chaque écran a été inspecté sur capture réelle en 1320×860 puis en 1040×1800 (écran portrait), avec les états vide, données réelles et démo.

| Date | Écran | États examinés | Corrections | Limites |
|---|---|---|---|---|
| 30/10 | Accueil « À redécouvrir » | démo, bibliothèque réelle, aucun souvenir du jour, médias non datés | accent bleu système remplacé par le brun Memo ; barre de titre accordée ; sections à 20 px ; badge démo en casse normale ; état vide utile | données réelles éparses sur le corpus ; pas de recette lecteur d'écran |
| 30/10 | Bibliothèque | photos, vidéos sans aperçu, date inconnue, filtres, sélection | champ de recherche élargi ; placeholder explicite « aperçu à générer » ; compteur et états vides ; actions persistées (favori, exclusion, date, lieu) | ligne de séparation des mois héritée du groupement natif ; virtualisation non mesurée à 10 000 éléments |
| 30/10 | Lecteur Story | photo, vidéo HEVC, calque, pause, progression, clavier | sortie vidéo embarquée via frames LibVLC + Skia (plus de fenêtre VLC séparée) ; barres de progression en grille ; EXIF photo ; sous-titre sans doublon | vidéos synthétiques non décodables sans aperçu ; pas de plein écran dédié |
| 30/10 | Albums | aucun album, album réel, création, renommage, suppression | état vide avec action ; masquage des actions sans sélection ; collections réelles | pas encore de réordonnancement par glisser-déposer |
| 30/10 | Récapitulatifs | année réelle, sélection vide, couverture partielle | couverture unique avec repli « Aperçu à générer » ; badge +N ; enregistrement en collection | pas de réordonnancement manuel dans l'interface |
| 30/10 | Recherche | texte, OCR, visuelle, personnes, vide | compteur de résultats ; sélecteur de référence ; résultats réels via empreintes ; groupes réels | OCR dépendant du pack de langue ; groupes de visages indicatifs |
| 30/10 | Carte | lieux réels, aucun lieu, édition | frontières Natural Earth réelles, remplacement du fond vide ; ajout de lieu depuis la fiche | pas de zoom/pan ; fond hors ligne uniquement |
| 30/10 | Imports | sélection, progression, bilan, archive corrompue, réimport | espaces réels, progression réelle, diagnostics, statuts traduits | pas de glisser-déposer de ZIP |
| 30/10 | Réglages | bibliothèque réelle, fuseau, sauvegarde, rappels, analyses | ouverture/création réelles ; fuseau IANA↔Windows corrigé ; sauvegarde/restauration ; rappels planifiés ; analyse locale | notifications dépendantes des réglages Windows ; pas de test sur machine N |

Contrôles Uncodixfy appliqués : aucun dégradé, verre, ombre décorative, pilule, surtitre en majuscules ou carte imbriquée ; rayons ≤ 8 px ; navigation native 240 px ; accent unique ; états vides réels. Exceptions documentées : police système Segoe UI Variable, focus natif conservé et teinté, densité bureau 32 px.

Restant à vérifier : parcours clavier complet, lecteur d'écran, 1024 px, réduction des animations, contrastes des états désactivés.
