namespace Memo.Core;

/// <summary>Nature d'une opération longue sur une bibliothèque.</summary>
public enum LibraryOperationKind
{
    Import,
    Restore,
    Analysis,
    TimeZoneChange,
    Maintenance,
}

/// <summary>
/// Levée lorsqu'une opération longue est déjà en cours sur la même
/// bibliothèque. Le message est destiné à être affiché tel quel.
/// </summary>
public sealed class LibraryOperationBusyException : InvalidOperationException
{
    public LibraryOperationBusyException(
        string libraryRoot,
        LibraryOperationKind current,
        LibraryOperationKind requested,
        DateTimeOffset startedAt)
        : base(BuildMessage(libraryRoot, current, requested, startedAt))
    {
        LibraryRoot = libraryRoot;
        Current = current;
        Requested = requested;
        StartedAt = startedAt;
    }

    public string LibraryRoot { get; }
    public LibraryOperationKind Current { get; }
    public LibraryOperationKind Requested { get; }
    public DateTimeOffset StartedAt { get; }

    internal static string BuildMessage(
        string libraryRoot,
        LibraryOperationKind current,
        LibraryOperationKind requested,
        DateTimeOffset startedAt)
    {
        var since = startedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        return $"Une opération est déjà en cours sur cette bibliothèque : " +
               $"{Describe(current)} depuis {since}. " +
               $"Attendez qu'elle se termine avant de lancer {Describe(requested)}.";
    }

    internal static string Describe(LibraryOperationKind kind) => kind switch
    {
        LibraryOperationKind.Import => "un import",
        LibraryOperationKind.Restore => "une restauration",
        LibraryOperationKind.Analysis => "une analyse",
        LibraryOperationKind.TimeZoneChange => "un changement de fuseau",
        _ => "une opération de maintenance",
    };
}

/// <summary>
/// Bail exclusif sur une bibliothèque. À libérer avec <c>using</c> ou
/// <c>await using</c>. Les acquisitions imbriquées dans le même flux
/// asynchrone sont autorisées et ne comptent qu'une fois dans le registre.
/// </summary>
public sealed class LibraryOperationLease : IDisposable, IAsyncDisposable
{
    private LibraryOperationGate.Holder? _holder;

    internal LibraryOperationLease(LibraryOperationGate.Holder holder)
    {
        _holder = holder;
    }

    public string LibraryRoot => _holder?.Root ?? string.Empty;
    public LibraryOperationKind Kind => _holder?.Kind ?? LibraryOperationKind.Maintenance;
    public bool IsActive => _holder is not null;

    public void Dispose()
    {
        var holder = Interlocked.Exchange(ref _holder, null);
        if (holder is not null)
        {
            LibraryOperationGate.Release(holder);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Verrou applicatif unique par bibliothèque pour les opérations longues qui
/// écrivent dans le catalogue ou le figent (import, restauration, analyse,
/// changement de fuseau, maintenance).
///
/// Le verrou est coopératif et purement local au processus : il ne bloque
/// jamais les lectures, qui continuent d'ouvrir leurs propres connexions
/// SQLite. Une seconde opération longue sur la même bibliothèque échoue
/// immédiatement avec un message clair plutôt que d'attendre silencieusement.
/// La clé est le chemin absolu de la bibliothèque ; deux bibliothèques
/// distinctes restent indépendantes.
/// </summary>
public static class LibraryOperationGate
{
    private sealed class Registry
    {
        public Dictionary<string, Holder> Active { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class Holder
    {
        public Holder(string root, LibraryOperationKind kind, DateTimeOffset startedAt)
        {
            Root = root;
            Kind = kind;
            StartedAt = startedAt;
            Depth = 1;
        }

        public string Root { get; }
        public LibraryOperationKind Kind { get; }
        public DateTimeOffset StartedAt { get; }
        public int Depth { get; set; }
    }

    private static readonly object Sync = new();
    private static readonly Registry Operations = new();

    // Permet à une même opération d'acquérir le verrou plusieurs fois sans se
    // bloquer elle-même (AppState -> ImportService -> catalogue).
    private static readonly AsyncLocal<Holder?> Ambient = new();

    /// <summary>Vrai si une opération longue est en cours sur cette bibliothèque.</summary>
    public static bool IsBusy(string libraryRoot, out string? message)
    {
        var root = Normalize(libraryRoot);
        lock (Sync)
        {
            if (Operations.Active.TryGetValue(root, out var holder))
            {
                var since = holder.StartedAt.ToLocalTime().ToString(
                    "HH:mm:ss",
                    System.Globalization.CultureInfo.CurrentCulture);
                message = $"Une opération est déjà en cours sur cette bibliothèque : " +
                          $"{LibraryOperationBusyException.Describe(holder.Kind)} depuis {since}.";
                return true;
            }
        }

        message = null;
        return false;
    }

    /// <summary>
    /// Tente d'acquérir le verrou sans attendre. Retourne null et un message
    /// français si une autre opération est déjà en cours.
    /// </summary>
    public static LibraryOperationLease? TryAcquire(
        string libraryRoot,
        LibraryOperationKind kind,
        out string? busyMessage)
    {
        var root = Normalize(libraryRoot);
        lock (Sync)
        {
            var ambient = Ambient.Value;
            if (ambient is not null
                && string.Equals(ambient.Root, root, StringComparison.OrdinalIgnoreCase)
                && Operations.Active.TryGetValue(root, out var active)
                && ReferenceEquals(active, ambient))
            {
                active.Depth++;
                busyMessage = null;
                return new LibraryOperationLease(active);
            }

            if (Operations.Active.TryGetValue(root, out var current))
            {
                busyMessage = LibraryOperationBusyException.BuildMessage(
                    current.Root,
                    current.Kind,
                    kind,
                    current.StartedAt);
                return null;
            }

            var holder = new Holder(root, kind, DateTimeOffset.UtcNow);
            Operations.Active[root] = holder;
            Ambient.Value = holder;
            busyMessage = null;
            return new LibraryOperationLease(holder);
        }
    }

    /// <summary>
    /// Acquiert le verrou ou lève <see cref="LibraryOperationBusyException"/>.
    /// </summary>
    public static LibraryOperationLease Acquire(string libraryRoot, LibraryOperationKind kind)
    {
        var lease = TryAcquire(libraryRoot, kind, out var busyMessage);
        if (lease is not null)
        {
            return lease;
        }

        var root = Normalize(libraryRoot);
        LibraryOperationKind current;
        DateTimeOffset startedAt;
        lock (Sync)
        {
            if (Operations.Active.TryGetValue(root, out var holder))
            {
                current = holder.Kind;
                startedAt = holder.StartedAt;
            }
            else
            {
                // Course rare : le verrou s'est libéré entre les deux lectures.
                return Acquire(root, kind);
            }
        }

        throw new LibraryOperationBusyException(root, current, kind, startedAt);
    }

    internal static void Release(Holder holder)
    {
        lock (Sync)
        {
            if (!Operations.Active.TryGetValue(holder.Root, out var current)
                || !ReferenceEquals(current, holder))
            {
                return;
            }

            holder.Depth--;
            if (holder.Depth <= 0)
            {
                Operations.Active.Remove(holder.Root);
            }
        }
    }

    private static string Normalize(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            throw new ArgumentException(
                "Le chemin de la bibliothèque est obligatoire pour verrouiller une opération.",
                nameof(libraryRoot));
        }

        var full = Path.GetFullPath(libraryRoot);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
