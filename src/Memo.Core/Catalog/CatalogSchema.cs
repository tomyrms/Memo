namespace Memo.Core;

/// <summary>Migration versionnée du catalogue. Le SQL doit rester idempotent.</summary>
internal sealed record CatalogMigration(int Version, string Name, string Sql);

/// <summary>
/// Description du schéma du catalogue et de ses migrations.
///
/// La version 1 correspond au socle livré avec Memo 0.1.0 (elle n'est jamais
/// rejouée sur une base existante : <c>PRAGMA user_version</c> vaut déjà 1).
/// Chaque version suivante ajoute une migration idempotente et une ligne dans
/// la table <c>schema_migrations</c>. Une base créée par une version plus
/// récente est refusée à l'ouverture.
/// </summary>
internal static class CatalogSchema
{
    public const int CurrentVersion = 2;

    public const string MigrationsTable = "schema_migrations";

    /// <summary>Table de suivi des migrations appliquées.</summary>
    public const string CreateMigrationsTable = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            applied_at TEXT NOT NULL
        );
        """;

    /// <summary>Socle initial, compatible avec les catalogues 0.1.0.</summary>
    public const string Baseline = """
        CREATE TABLE IF NOT EXISTS spaces (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            account_hint TEXT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS batches (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            space_id INTEGER NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
            started_at TEXT NOT NULL,
            completed_at TEXT NULL,
            status TEXT NOT NULL,
            note TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS archives (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_id INTEGER NULL REFERENCES batches(id) ON DELETE SET NULL,
            space_id INTEGER NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
            source_path TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            size INTEGER NOT NULL,
            status TEXT NOT NULL,
            entry_count INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            UNIQUE (space_id, sha256)
        );

        CREATE TABLE IF NOT EXISTS source_files (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            space_id INTEGER NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
            archive_id INTEGER NULL REFERENCES archives(id) ON DELETE CASCADE,
            entry_path TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            size INTEGER NOT NULL,
            declared_ext TEXT NOT NULL,
            detected_format TEXT NOT NULL,
            role TEXT NOT NULL,
            created_at TEXT NOT NULL,
            UNIQUE (space_id, archive_id, entry_path)
        );

        CREATE TABLE IF NOT EXISTS assets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            sha256 TEXT NOT NULL UNIQUE,
            format TEXT NOT NULL,
            width INTEGER NULL,
            height INTEGER NULL,
            rotation INTEGER NOT NULL DEFAULT 0,
            duration_ms INTEGER NULL,
            has_audio INTEGER NOT NULL DEFAULT 0,
            stored_path TEXT NOT NULL,
            size INTEGER NOT NULL,
            creation_time_utc TEXT NULL,
            exif_date TEXT NULL,
            exif_raw TEXT NULL,
            exif_source INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS memories (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            space_id INTEGER NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
            external_key TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'unknown',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE (space_id, external_key)
        );

        CREATE TABLE IF NOT EXISTS memory_assets (
            memory_id INTEGER NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            role TEXT NOT NULL,
            PRIMARY KEY (memory_id, asset_id, role)
        );

        CREATE TABLE IF NOT EXISTS memory_files (
            memory_id INTEGER NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            source_file_id INTEGER NOT NULL REFERENCES source_files(id) ON DELETE CASCADE,
            role TEXT NOT NULL,
            PRIMARY KEY (memory_id, source_file_id)
        );

        CREATE TABLE IF NOT EXISTS date_candidates (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            memory_id INTEGER NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            raw TEXT NOT NULL,
            source INTEGER NOT NULL,
            meaning INTEGER NOT NULL,
            precision INTEGER NOT NULL,
            timezone TEXT NULL,
            instant_utc TEXT NULL,
            local_date TEXT NULL,
            local_time TEXT NULL,
            confidence REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS date_overrides (
            memory_id INTEGER PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,
            local_date TEXT NOT NULL,
            precision INTEGER NOT NULL,
            note TEXT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS locations (
            memory_id INTEGER PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,
            lat REAL NOT NULL,
            lon REAL NOT NULL,
            label TEXT NULL,
            source TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS favorites (
            memory_id INTEGER PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS exclusions (
            memory_id INTEGER NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            scope TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (memory_id, scope)
        );

        CREATE TABLE IF NOT EXISTS collections (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            kind INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS collection_items (
            collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            memory_id INTEGER NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            PRIMARY KEY (collection_id, memory_id)
        );

        CREATE TABLE IF NOT EXISTS suggestion_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            memory_id INTEGER NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            proposed_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS analysis_ocr (
            memory_id INTEGER PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,
            text TEXT NOT NULL,
            engine TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS analysis_visual (
            memory_id INTEGER PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,
            hash INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS person_groups (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS faces (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            memory_id INTEGER NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
            group_id INTEGER NULL REFERENCES person_groups(id) ON DELETE SET NULL,
            x INTEGER NOT NULL,
            y INTEGER NOT NULL,
            w INTEGER NOT NULL,
            h INTEGER NOT NULL,
            descriptor BLOB NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS thumbnails (
            memory_id INTEGER PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,
            relative_path TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS memory_notes (
            memory_id INTEGER PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,
            text TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_memories_space ON memories (space_id);
        CREATE INDEX IF NOT EXISTS ix_memories_status ON memories (status);
        CREATE INDEX IF NOT EXISTS ix_source_files_archive ON source_files (archive_id);
        CREATE INDEX IF NOT EXISTS ix_source_files_sha ON source_files (sha256);
        CREATE INDEX IF NOT EXISTS ix_date_candidates_memory ON date_candidates (memory_id);
        CREATE INDEX IF NOT EXISTS ix_date_candidates_local_date ON date_candidates (local_date);
        CREATE INDEX IF NOT EXISTS ix_memory_files_source ON memory_files (source_file_id);
        CREATE INDEX IF NOT EXISTS ix_collection_items_position ON collection_items (collection_id, position);
        CREATE INDEX IF NOT EXISTS ix_suggestion_history_date ON suggestion_history (proposed_at);
        CREATE INDEX IF NOT EXISTS ix_faces_group ON faces (group_id);
        CREATE INDEX IF NOT EXISTS ix_exclusions_memory ON exclusions (memory_id);
        """;

    /// <summary>Migrations ordonnées, chacune idempotente et rejouable.</summary>
    public static readonly IReadOnlyList<CatalogMigration> Migrations = new CatalogMigration[]
    {
        new(1, "socle-0.1.0", Baseline),
        new(2, "journal-des-migrations", CreateMigrationsTable),
    };
}
