using System.Buffers;

namespace Memo.Core;

/// <summary>
/// Contrat interne d'adaptateur d'index de liens.
///
/// Aucun adaptateur réel n'est enregistré par défaut. Un format non validé ne doit jamais
/// s'activer sur une simple ressemblance : la détection exige un marqueur explicite propre au
/// format, et le registre refuse un adaptateur non validé sans option explicite.
/// </summary>
internal interface ILinkIndexAdapter
{
    /// <summary>Identifiant stable du format pris en charge.</summary>
    string Id { get; }

    /// <summary>
    /// Vrai uniquement lorsqu'un exemple réel autorisé a validé le format. Un adaptateur de test
    /// renvoie toujours <c>false</c>.
    /// </summary>
    bool IsValidated { get; }

    /// <summary>Détection stricte du format, à partir d'un marqueur explicite.</summary>
    LinkIndexDetection Detect(ReadOnlySpan<byte> content);

    /// <summary>Lecture des entrées et validation des URL selon <paramref name="options"/>.</summary>
    LinkIndexResult Read(ReadOnlyMemory<byte> content, LinkIndexReadOptions options);
}

/// <summary>
/// Registre d'adaptateurs. <see cref="Empty"/> est le seul état utilisable par défaut : aucun
/// format d'index n'est activé tant qu'un exemple réel n'a pas été validé.
/// </summary>
internal sealed class LinkIndexAdapterRegistry
{
    private readonly List<ILinkIndexAdapter> _adapters;

    public static LinkIndexAdapterRegistry Empty { get; } =
        new(Array.Empty<ILinkIndexAdapter>(), allowUnvalidatedAdapters: false);

    /// <param name="adapters">Adaptateurs à enregistrer.</param>
    /// <param name="allowUnvalidatedAdapters">
    /// Autorise explicitement un adaptateur dont <see cref="ILinkIndexAdapter.IsValidated"/> vaut
    /// <c>false</c>. Réservé aux tests et aux serveurs locaux.
    /// </param>
    public LinkIndexAdapterRegistry(
        IEnumerable<ILinkIndexAdapter> adapters,
        bool allowUnvalidatedAdapters = false)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = new List<ILinkIndexAdapter>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!adapter.IsValidated && !allowUnvalidatedAdapters)
            {
                throw new InvalidOperationException(
                    $"L'adaptateur « {adapter.Id} » n'est pas validé et ne peut pas être activé sans option explicite.");
            }

            if (!ids.Add(adapter.Id))
            {
                throw new InvalidOperationException(
                    $"L'adaptateur « {adapter.Id} » est enregistré deux fois.");
            }

            _adapters.Add(adapter);
        }
    }

    public IReadOnlyList<ILinkIndexAdapter> Adapters => _adapters;

    public LinkIndexDetection Detect(ReadOnlySpan<byte> content)
    {
        if (_adapters.Count == 0)
        {
            return new LinkIndexDetection(false, null, "Aucun adaptateur d'index n'est activé.");
        }

        LinkIndexDetection? match = null;
        foreach (var adapter in _adapters)
        {
            var detection = adapter.Detect(content);
            if (!detection.Detected)
            {
                continue;
            }

            if (match is not null)
            {
                return new LinkIndexDetection(
                    false,
                    null,
                    "Plusieurs adaptateurs reconnaissent le contenu ; aucun n'est choisi.");
            }

            match = detection with { AdapterId = adapter.Id };
        }

        return match ?? new LinkIndexDetection(false, null, "Aucun adaptateur ne reconnaît ce contenu.");
    }

    public LinkIndexResult Read(ReadOnlyMemory<byte> content, LinkIndexReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_adapters.Count == 0)
        {
            return LinkIndexResult.NotRecognized(
                "none",
                "index.no-adapter",
                "Aucun adaptateur d'index n'est activé.");
        }

        var matches = new List<ILinkIndexAdapter>();
        foreach (var adapter in _adapters)
        {
            if (adapter.Detect(content.Span).Detected)
            {
                matches.Add(adapter);
            }
        }

        if (matches.Count == 0)
        {
            return LinkIndexResult.NotRecognized(
                "none",
                "index.unknown-shape",
                "Le contenu ne correspond à aucun adaptateur activé.");
        }

        if (matches.Count > 1)
        {
            return LinkIndexResult.NotRecognized(
                "none",
                "index.ambiguous-shape",
                "Plusieurs adaptateurs reconnaissent le contenu ; aucun n'est choisi.");
        }

        return matches[0].Read(content, options);
    }

    public async Task<LinkIndexResult> ReadAsync(
        Stream content,
        LinkIndexReadOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var memory = new MemoryStream();
            while (true)
            {
                var read = await content
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (memory.Length + read > options.MaxIndexBytes)
                {
                    return LinkIndexResult.NotRecognized(
                        "none",
                        "index.too-large",
                        $"Index refusé : taille supérieure à {options.MaxIndexBytes} octets.");
                }

                memory.Write(buffer, 0, read);
            }

            return Read(memory.ToArray(), options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
