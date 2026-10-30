# Rappels Windows

Ce dossier contient l'implémentation des rappels quotidiens facultatifs de Memo.
Aucun fichier existant n'est modifié : l'intégration dans `App.xaml.cs`,
`MainWindow.xaml.cs` et `SettingsPage` est faite par l'appelant.

## Fichiers

| Fichier | Rôle |
|---|---|
| `ToastService.cs` | Notifications locales Windows App SDK, enregistrement, activation, mode discret. |
| `ReminderScheduler.cs` | Création, suppression et interrogation de la tâche planifiée quotidienne via `schtasks.exe`. |
| `ReminderRunner.cs` | Logique de `Memo.exe --reminder <bibliothèque>` et détection de l'activation au démarrage. |
| `ReminderState.cs` | État local `%LOCALAPPDATA%\Memo\reminders.json` (dernier jour notifié par bibliothèque). |

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
  machine. L'heure de déclenchement, elle, est l'heure locale Windows (voir
  « Limites »).
- La notification est taguée `d-yyyymmdd` dans le groupe `memo-reminder` : une
  nouvelle notification du même jour remplace l'ancienne dans le Centre de
  notifications.

### Activation

`ToastService.Current.Activated` est levé avec un `ToastActivatedEventArgs` :

- `Day` (`DateOnly?`) et `DayText` (`yyyy-MM-dd`) pour ouvrir « Ce jour-là » ;
- `LocalPath` si une charge utile contient un argument `library` (Memo n'en
  produit pas aujourd'hui).

L'événement est levé sur le thread du callback WinRT. L'abonné doit revenir sur
le thread UI :

```csharp
ToastService.Current.Activated += (_, e) =>
{
    var window = App.MainWindow;
    window?.DispatcherQueue.TryEnqueue(() =>
    {
        // Ouvrir la sélection du jour e.Day / e.DayText.
    });
};
```

Quand Memo n'est pas en cours d'exécution, Windows lance `Memo.exe` par
activation COM. `ReminderRunner.TryHandleStartupAsync()` appelle `Register()`
puis lit `AppInstance.GetCurrent().GetActivatedEventArgs()` et lève le même
événement. L'appelant peut aussi utiliser directement
`startup.Activation`.

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
- `EnableAsync` est idempotent (`/F` recrée la tâche).
- `DisableAsync` supprime la tâche. Une tâche déjà absente est considérée comme
  désactivée.
- `GetStatusAsync` retourne la présence, l'heure de début, la prochaine
  exécution, la dernière exécution et le dernier résultat. Les libellés
  localisés de `schtasks` sont lus en français et en anglais ; la sortie brute
  reste disponible dans `RawOutput`.
- Si `schtasks.exe` est absent, refusé par une stratégie ou retourne une erreur,
  l'appel retourne `Success = false` avec un message français. Memo reste
  utilisable : aucun réglage n'est modifié par le service de rappels lui-même.

### Runner

`Memo.exe --reminder "<bibliothèque>"` :

1. ouvre la bibliothèque (`MemoLibrary.OpenAsync`) ;
2. vérifie `AppSettings.RemindersEnabled` ;
3. calcule le jour dans le fuseau de la bibliothèque ;
4. vérifie `%LOCALAPPDATA%\Memo\reminders.json` pour ne pas répéter la même
   journée ;
5. appelle `Discovery.GetThisDayAsync` avec
   `IncludeUncertainDates = AppSettings.IncludeUncertainDatesInRecaps` ;
6. retire les souvenirs exclus (`IsExcluded`), en seconde barrière ;
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

## Intégration attendue

Au démarrage de l'application, abonner l'événement **avant** d'enregistrer le
service, puis traiter le démarrage :

```csharp
ToastService.Current.Activated += (_, e) =>
{
    var window = App.MainWindow;
    window?.DispatcherQueue.TryEnqueue(() =>
    {
        // Ouvrir la sélection du jour e.Day / e.DayText.
    });
};

ToastService.Current.Register();

var startup = await ReminderRunner.TryHandleStartupAsync();

if (startup.Kind == ReminderStartupKind.ReminderInvocation)
{
    // Mode tâche planifiée : ne pas afficher la fenêtre.
    Environment.Exit(startup.Reminder!.ExitCode);
}

// Créer la fenêtre. Si startup.Kind == NotificationActivation, ouvrir
// startup.Activation.Day / DayText dans « Ce jour-là » (ou laisser
// l'événement Activated le faire, mais pas les deux).
```

Activation/désactivation depuis les réglages :

```csharp
var result = await ReminderScheduler.EnableAsync(
    Environment.ProcessPath!,          // Memo.exe
    library.Info.RootPath,
    settings.ReminderTime);            // "HH:mm"

if (!result.Success)
{
    // Afficher result.Message à l'utilisateur.
}

var disabled = await ReminderScheduler.DisableAsync(library.Info.Id);
var status = await ReminderScheduler.GetStatusAsync(library.Info.Id);
```

`EnableAsync` lit lui-même `library.json` pour obtenir l'identifiant de
bibliothèque. Il n'est donc pas nécessaire de le passer.

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
  l'affichage. `ToastService.LastError` et le journal local donnent la cause
  connue.
- **Application élevée.** Les notifications Windows ne fonctionnent pas pour
  une application lancée en administrateur. Memo doit être lancé normalement.
- **Instance unique.** Si Memo est déjà ouvert, le clic sur une notification
  est traité par le processus existant via `NotificationInvoked`. Si Memo est
  fermé, Windows démarre un nouveau processus. La redirection vers une instance
  existante (`AppInstance.FindOrRegisterForKey` / `RedirectActivationToAsync`)
  reste à la charge de l'intégration UI.
- **Bibliothèque déplacée.** La tâche contient le chemin absolu de la
  bibliothèque. Après un déplacement ou un renommage, il faut désactiver puis
  réactiver le rappel. Une restauration sur un autre PC ne recrée pas la tâche.
- **Identifiant de bibliothèque.** Le nom de tâche dépend du GUID contenu dans
  `library.json`. Une bibliothèque restaurée avec un nouvel identifiant laisse
  l'ancienne tâche derrière elle ; la désinstallation doit supprimer les
  tâches `Memo.Reminder.*` orphelines.

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

```powershell
# 1. Supprimer toutes les tâches de rappel Memo.
Get-ScheduledTask -TaskName "Memo.Reminder.*" -ErrorAction SilentlyContinue |
    Unregister-ScheduledTask -Confirm:$false

# 2. Supprimer l'état local et les journaux.
Remove-Item "$env:LOCALAPPDATA\Memo\reminders.json" -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\Memo\reminders.log" -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\Memo\notifications.log" -ErrorAction SilentlyContinue

# 3. Nettoyer l'enregistrement des notifications si l'application est encore
#    installée : appeler ToastService.Current.UnregisterAll() une dernière fois.
#    Sinon, l'entrée d'activation COM reste dans le profil utilisateur.
```

Après désinstallation, vérifier qu'aucune tâche ne subsiste :

```powershell
schtasks /Query /FO LIST | Select-String "Memo.Reminder."
```

## Confidentialité et réseau

- Aucune connexion réseau n'est ouverte par ces fichiers.
- Aucun identifiant n'est demandé.
- Les notifications ne contiennent qu'un jour ISO et une action.
- Les fichiers d'état et de diagnostic restent sous
  `%LOCALAPPDATA%\Memo\` et ne sont jamais inclus dans une sauvegarde de
  bibliothèque.
