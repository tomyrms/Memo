using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Memo.Core;

/// <summary>
/// Applique et vérifie les migrations versionnées du catalogue.
///
/// La version de référence est <c>PRAGMA user_version</c>, doublée par la table
/// <c>schema_migrations</c> qui conserve l'historique. Les deux sources sont
/// lues : une base dont l'une des deux annonce une version plus récente que
/// l'application est refusée avant toute écriture. Une migration est exécutée
/// dans une transaction immédiate, avec un SQL idempotent, puis inscrite dans
/// le journal. Rejouer une migration sur une copie de catalogue est donc sans
/// effet de bord.
/// </summary>
internal static class CatalogMigrations
{
    /// <summary>
    /// Met le catalogue au niveau courant et retourne la version finale.
    /// Lève <see cref="InvalidOperationException"/> si la base vient d'une
    /// version plus récente de Memo.
    /// </summary>
    public static async Task<int> ApplyAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken ct)
    {
        var state = await ReadStateAsync(connection, null, ct).ConfigureAwait(false);
        EnsureSupported(state, databasePath);

        // Chemin nominal : les deux sources annoncent la même version, rien à
        // écrire ni verrouiller. Un désaccord (même sans migration à jouer) est
        // réparé sous transaction pour que PRAGMA user_version et le journal
        // restent alignés.
        if (state.JournalExists
            && state.DeclaredVersion == CatalogSchema.CurrentVersion
            && state.JournalVersion == CatalogSchema.CurrentVersion)
        {
            return CatalogSchema.CurrentVersion;
        }

        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        // Une autre instance a pu migrer entre la lecture initiale et le verrou :
        // on relit l'état sous transaction avant de décider.
        state = await ReadStateAsync(connection, transaction, ct).ConfigureAwait(false);
        EnsureSupported(state, databasePath);

        // Le journal est créé avant la première migration afin de pouvoir
        // enregistrer chaque version, y compris le socle 0.1.0 rétroactif.
        await ExecuteAsync(
            connection,
            transaction,
            CatalogSchema.CreateMigrationsTable,
            ct).ConfigureAwait(false);

        // Les versions déjà appliquées sont inscrites au journal si elles
        // manquent : c'est le cas d'un catalogue 0.1.0, qui n'avait que
        // PRAGMA user_version.
        foreach (var migration in CatalogSchema.Migrations)
        {
            if (migration.Version <= state.HighestVersion)
            {
                await RecordAsync(connection, transaction, migration, ct).ConfigureAwait(false);
            }
        }

        // Les migrations restantes sont rejouées dans l'ordre. Leur SQL est
        // idempotent, ce qui rend une reprise après incident sans danger.
        foreach (var migration in CatalogSchema.Migrations)
        {
            if (migration.Version <= state.HighestVersion)
            {
                continue;
            }

            await ExecuteAsync(connection, transaction, migration.Sql, ct).ConfigureAwait(false);
            await RecordAsync(connection, transaction, migration, ct).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"PRAGMA user_version = {CatalogSchema.CurrentVersion};",
            ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return CatalogSchema.CurrentVersion;
    }

    private sealed record MigrationState(long DeclaredVersion, long JournalVersion, bool JournalExists)
    {
        public long HighestVersion => Math.Max(DeclaredVersion, JournalVersion);
    }

    private static async Task<MigrationState> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        var declared = await ScalarLongAsync(
            connection,
            transaction,
            "PRAGMA user_version;",
            ct).ConfigureAwait(false);

        var journalExists = await ScalarLongAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';",
            ct).ConfigureAwait(false) > 0;

        long journalVersion = 0;
        if (journalExists)
        {
            try
            {
                journalVersion = await ScalarLongAsync(
                    connection,
                    transaction,
                    "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;",
                    ct).ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException(
                    "Le journal des migrations du catalogue est illisible : " + ex.Message,
                    ex);
            }
        }

        return new MigrationState(declared, journalVersion, journalExists);
    }

    private static void EnsureSupported(MigrationState state, string databasePath)
    {
        if (state.HighestVersion <= CatalogSchema.CurrentVersion)
        {
            return;
        }

        var detail = state.DeclaredVersion == state.JournalVersion
            ? state.HighestVersion.ToString(CultureInfo.InvariantCulture)
            : $"{state.HighestVersion} (user_version = {state.DeclaredVersion}, journal = {state.JournalVersion})";
        throw new InvalidOperationException(
            $"Le catalogue « {databasePath} » utilise une version de schéma plus récente ({detail}) " +
            $"que cette version de Memo ({CatalogSchema.CurrentVersion}). " +
            "Mettez Memo à jour avant d'ouvrir cette bibliothèque ; aucun fichier n'a été modifié.");
    }

    private static async Task RecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CatalogMigration migration,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
            VALUES (@version, @name, @applied);
            """,
            ct,
            ("@version", migration.Version),
            ("@name", migration.Name),
            ("@applied", DateText.NowUtc())).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
