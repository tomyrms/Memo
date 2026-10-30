# Rappels Windows

Rappels quotidiens facultatifs de Memo. Aucun service Windows tiers, aucun
démarrage avec Windows et aucune connexion réseau ne sont utilisés.

## Fichiers

| Fichier | Rôle |
|---|---|
| `ToastService.cs` | Notifications locales Windows App SDK, enregistrement, activation, mode discret, journaux. |
| `ReminderScheduler.cs` | Création, suppression et interrogation de la tâche planifiée quotidienne via `schtasks.exe`. |
| `ReminderRunner.cs` | Logique de `Memo.exe --reminder <bibliothèque>`, détection de l'activation au démarrage et mode test. |
| `ReminderState.cs` | État local `%LOCALAPPDATA%\Memo\reminders.json` (dernier jour notifié par bibliothèque). |

L'intégration est faite dans `App.xaml.cs`, `MainWindow.xaml.cs` et
`Pages/SettingsPage.xaml(.cs)`.

## Comportement réel

### Notification

- Titre normal : **Ce jour-là**.
- Texte normal : **N souvenirs vous attendent** (ou « 1 souvenir vous attend »).
- Miniature : la première miniature locale disponible, uniquement en mode normal.
- Mode discret : titre **Memo**, texte **Vos souvenirs sont prêts.**, aucune
  miniature, aucun lieu, aucun nom de personne.
- Charge utile : `action=open-this-day` et `day=yyyy-MM-dd`. Rien d'autre.
  Le nom de la bibliothèque, les lieux, les personnes et les chemins ne sont
  jamais inclus dans la notification.
- Le jour est calculé dans le fuseau de la bibliothèque, pas dans celui de la
  machine. L'heure de déclenchement, elle, est l'heure locale Windows.
- La notification est taguée `d-yyyymmdd` dans le groupe `memo-reminder` : une
  nouvelle notification du même jour remplace l'ancienne dans le Centre de
  notifications.

### Activation par clic

Le clic est traité par `ToastService.Activated`, puis par `App` :

1. le jour ISO de la charge utile est lu (`ToastActivatedEventArgs.Day`) ;
2. l'action est ramenée sur le thread UI ;
3. la fenêtre existante est ramenée au premier plan, ou recréée si elle a été
   fermée alors que le processus tournait encore ;
4. `MainWindow.ShowDay(day)` ouvre une Story sur « Ce jour-là » : même jour et
   même mois dans les années antérieures, hors souvenirs masqués et hors
   calques seuls. L'année de la charge utile identifie le jour ; le calcul des
   anniversaires va jusqu'à l'année courante de la bibliothèque, de sorte qu'une
   notification conservée plusieurs jours reste exploitable.

Quand la fenêtre Memo est fermée et que Windows démarre un nouveau processus,
`ReminderRunner.TryHandleStartupAsync` lit `AppInstance.GetActivatedEventArgs()`
et retourne l'activation à `App`, qui l'ouvre une seule fois. Si la bibliothèque
n'est pas encore chargée, `ShowDay` mémorise le jour et rejoue la sélection dès
que `ShellPage.Ready` est levé : la Story ne dépend pas d'un chargement déjà
terminé.

Une instance unique est enregistrée sous la clé `Memo.Main`
(`AppInstance.FindOrRegisterForKey`). Un second lancement — y compris un clic
de notification alors que Memo est déjà ouvert — est redirigé vers la première
instance (`RedirectActivationToAsync`), qui traite l'activation puis ramène sa
fenêtre. Le processus secondaire quitte sans créer de fenêtre. Si AppLifecycle
est indisponible, Memo retombe sur le comportement multi-instance : le clic est
alors traité par le processus qui a enregistré les notifications, ou par un
nouveau processus.

Le processus lancé par la tâche planifiée (`--reminder`) ne rejoint jamais
l'instance interactive : il affiche la notification puis se termine.

### Fenêtre fermée

- À l'heure choisie, le Planificateur de tâches Windows lance
  `Memo.exe --reminder "<bibliothèque>"`.
- Ce processus ouvre la bibliothèque, calcule la sélection, affiche le toast et
  quitte avec un code de sortie. **Aucune fenêtre n'est créée** et aucune icône
  de zone de notification n'est ajoutée.
- Si l'utilisateur clique sur le toast alors que Memo est fermé, Windows démarre
  Memo par activation ; la Story « Ce jour-là » s'ouvre après le chargement de
  la bibliothèque.
- Si Memo est déjà ouvert, le clic est remis à la première instance et ramène sa
  fenêtre au premier plan.
- Il n'y a **aucun démarrage avec Windows** et aucun processus résident : la
  seule persistance est la tâche planifiée, créée uniquement après activation
  volontaire.
- Si la fenêtre est fermée alors que le processus reste vivant (comportement
  WinUI variable selon la version), le clic suivant recrée une fenêtre. Si le
  processus a quitté, Windows en démarre un nouveau.

### Planification

Nom de tâche : `Memo.Reminder.<guid de bibliothèque>` (format `D` du GUID).

Commande créée :

```text
schtasks /Create /SC DAILY /TN "Memo.Reminder.<guid>" ^
  /TR "\"<Memo.exe>\" --reminder \"<bibliothèque>\"" ^
  /ST HH:mm /RL LIMITED /F
```

- Utilisateur courant, sans `/RU` ni `/RP` : aucun mot de passe n'est demandé
  ni stocké.
- `/RL LIMITED` garantit une exécution non élevée, même si Memo a été lancé
  avec un jeton administrateur.
- `EnableAsync` est idempotent : `/F` recrée la tâche. Changer l'heure appelle
  directement `EnableAsync`, sans supprimer la tâche au préalable.
- `DisableAsync` supprime la tâche. Une tâche déjà absente est considérée comme
  désactivée.
- `GetStatusAsync` retourne la présence, l'heure de début, la prochaine
  exécution, la dernière exécution, le dernier résultat et l'état Windows. Les
  libellés localisés de `schtasks` sont lus en français et en anglais ; la
  sortie brute reste disponible dans `RawOutput`.
- Si `schtasks.exe` est absent, refusé par une stratégie ou retourne une erreur,
  l'appel retourne `Success = false` avec un message français. Memo reste
  utilisable : aucun réglage n'est modifié par le service de rappels lui-même.

### Runner

`Memo.exe --reminder "<bibliothèque>"` :

1. ouvre la bibliothèque (`MemoLibrary.OpenAsync`) ;
2. vérifie `AppSettings.RemindersEnabled` ;
3. calcule le jour dans le fuseau de la bibliothèque ;
4. vérifie `%LOCALAPPDATA%\Memo\reminders.json` pour ne pas répéter la même
   journée, y compris après un redémarrage ;
5. appelle `Discovery.GetThisDayAsync` avec
   `IncludeUncertainDates = AppSettings.IncludeUncertainDatesInRecaps` ;
6. retire les souvenirs exclus (`IsExcluded`) en seconde barrière ; la
   découverte exclut déjà les souvenirs masqués et les calques seuls ;
7. enregistre la proposition avec `Catalog.RecordSuggestionAsync` ;
8. affiche la notification (normale ou discrète selon les réglages) ;
9. marque le jour comme notifié et quitte.

Le délai global est plafonné à **2 minutes** (100 secondes par défaut, plafond
de sécurité de 115 secondes). Codes de sortie :

| Code | Signification |
|---:|---|
| 0 | Notification affichée |
| 2 | Argument `--reminder` sans chemin valide |
| 3 | Rappels désactivés dans les réglages |
| 4 | Journée déjà notifiée |
| 5 | Aucun souvenir ce jour-là |
| 10 | Bibliothèque impossible à ouvrir ou erreur inattendue |
| 11 | Délai de 2 minutes dépassé, ou opération annulée |
| 12 | Notification impossible à afficher |

Les diagnostics sont ajoutés à `%LOCALAPPDATA%\Memo\reminders.log` et les
erreurs d'enregistrement/affichage à `%LOCALAPPDATA%\Memo\notifications.log`.
Ces fichiers restent locaux.

### Réglages

La carte **Rappels** affiche l'état réel, pas seulement la préférence :

- nom de la tâche et heure quotidienne, ou « Aucune tâche planifiée » ;
- prochaine exécution annoncée par Windows ;
- dernier résultat (`Succès`, `Jamais exécutée`, code) et dernière exécution ;
- état Windows de la tâche (prête, désactivée, etc.) ;
- bouton **Tester maintenant**.

Le bouton de test exécute le runner avec
`IgnoreRemindersSetting = true`, `IgnoreAlreadyNotified = true` et
`PersistState = false` : il affiche un toast immédiat, mais ne marque pas la
journée comme notifiée et n'altère pas l'historique des propositions. Le vrai
rappel du jour reste donc possible.

L'interrupteur, l'heure et le mode discret sont désactivés tant qu'aucune
bibliothèque n'est ouverte. Si l'activation échoue, l'interrupteur revient à
l'état réel et un message explicite est affiché. Si la désactivation échoue,
l'interrupteur reste actif, car la tâche peut encore se déclencher. Un
changement d'heure qui échoue rétablit l'ancienne heure.

## Limites

- **Session ouverte.** La tâche est créée sans mot de passe et s'exécute dans
  la session interactive de l'utilisateur. Elle ne se déclenche que si Windows
  est allumé et si l'utilisateur est connecté. Aucun service Windows tiers,
  aucun démarrage automatique caché et aucune élévation ne sont utilisés.
- **Veille et arrêt.** Si l'ordinateur est en veille à l'heure prévue, Windows
  peut lancer la tâche au réveil, en retard, ou l'ignorer selon les réglages
  d'alimentation. Si l'ordinateur est éteint, la tâche n'est pas rattrapée.
- **Fuseau.** L'heure `HH:mm` est interprétée par le Planificateur dans le
  fuseau local Windows. Le jour « Ce jour-là » est calculé dans le fuseau de la
  bibliothèque. Si les deux diffèrent, la notification peut concerner le jour
  de la bibliothèque à une heure locale différente. Ce comportement est
  volontaire : le fuseau de classement reste stable après un voyage.
- **Notifications désactivées.** Le mode Ne pas déranger, les réglages de
  notifications Windows ou une stratégie de groupe peuvent empêcher
  l'affichage. La tâche s'exécute quand même et le résultat est visible dans
  Réglages et dans le journal local. Le toast lui-même n'est alors pas
  observable ; c'est la limite principale de la validation sur une machine où
  les notifications sont coupées.
- **Application élevée.** Les notifications Windows ne fonctionnent pas pour
  une application lancée en administrateur. Memo doit être lancé normalement.
- **Instance unique.** La redirection repose sur Windows AppLifecycle. Si elle
  échoue, Memo reste utilisable mais peut ouvrir une seconde instance ; le clic
  de notification est alors traité par le processus qui a enregistré les
  notifications.
- **Bibliothèque déplacée.** La tâche contient le chemin absolu de la
  bibliothèque. Après un déplacement ou un renommage, il faut désactiver puis
  réactiver le rappel. Une restauration sur un autre PC ne recrée pas la tâche.
- **Identifiant de bibliothèque.** Le nom de tâche dépend du GUID contenu dans
  `library.json`. Une bibliothèque restaurée avec un nouvel identifiant laisse
  l'ancienne tâche derrière elle ; la désinstallation ou le nettoyage manuel
  doit supprimer les tâches `Memo.Reminder.*` orphelines.
- **Test non observable.** Si les notifications Windows sont désactivées, le
  bouton de test affiche un diagnostic (`ToastService.LastError`) au lieu d'un
  toast. Aucun test automatisé ne peut remplacer une vérification visuelle sur
  une session Windows où les toasts sont autorisés.

## Vérification

Remplacer `<guid>` par l'identifiant de la bibliothèque.

```powershell
# La tâche existe-t-elle et quel est son état ?
schtasks /Query /TN "Memo.Reminder.<guid>" /V /FO LIST

# Variante PowerShell, plus lisible.
Get-ScheduledTask -TaskName "Memo.Reminder.<guid>"
Get-ScheduledTaskInfo -TaskName "Memo.Reminder.<guid>"

# Lancement immédiat pour tester sans attendre l'heure prévue.
schtasks /Run /TN "Memo.Reminder.<guid>"

# Journaux du runner et des notifications.
Get-Content "$env:LOCALAPPDATA\Memo\reminders.log" -Tail 20
Get-Content "$env:LOCALAPPDATA\Memo\notifications.log" -Tail 20

# Historique du Planificateur (selon la stratégie locale).
Get-WinEvent -LogName Microsoft-Windows-TaskScheduler/Operational -MaxEvents 100 |
    Where-Object { $_.Message -like "*Memo.Reminder*" } |
    Select-Object TimeCreated, Id, LevelDisplayName, Message
```

Événements utiles : `200` (action démarrée), `201` (action terminée), `102`
(tâche terminée). Le « Dernier résultat » `0` indique un succès ; `267011`
signifie que la tâche n'a jamais été exécutée.

## Désinstallation

1. Dans Memo, désactiver le rappel de la bibliothèque ouverte. Cela supprime la
   tâche et son entrée de réglage.
2. Supprimer les éventuelles tâches orphelines (bibliothèque déplacée ou
   identifiant changé) :

```powershell
Get-ScheduledTask -TaskName "Memo.Reminder.*" -ErrorAction SilentlyContinue |
    Unregister-ScheduledTask -Confirm:$false
```

3. Supprimer l'état local et les journaux :

```powershell
Remove-Item "$env:LOCALAPPDATA\Memo\reminders.json" -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\Memo\reminders.log" -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\Memo\notifications.log" -ErrorAction SilentlyContinue
```

4. Tant que Memo est encore installé, appeler une dernière fois
   `ToastService.Current.UnregisterAll()` pour retirer l'enregistrement
   d'activation des notifications. Si l'application est déjà supprimée,
   l'entrée COM résiduelle pointe vers un exécutable absent ; une
   réinstallation suivie d'un nettoyage, ou le nettoyage du profil utilisateur,
   permet de la retirer.

Après désinstallation, vérifier qu'aucune tâche ne subsiste :

```powershell
Get-ScheduledTask -TaskName "Memo.Reminder.*" -ErrorAction SilentlyContinue |
    Select-Object TaskName, State
```

## Confidentialité et réseau

- Aucune connexion réseau n'est ouverte par ces fichiers.
- Aucun identifiant n'est demandé.
- Les notifications ne contiennent qu'un jour ISO et une action.
- Les fichiers d'état et de diagnostic restent sous
  `%LOCALAPPDATA%\Memo\` et ne sont jamais inclus dans une sauvegarde de
  bibliothèque.
