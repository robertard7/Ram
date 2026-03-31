using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using RAM.Models;
using SQLitePCL;

namespace RAM.Services;

public sealed class RamDbService
{
    private const string CurrentSchemaVersion = "16";
    private static readonly RamDataDisciplineService DataDisciplineService = new();
    private static readonly object SqliteRuntimeLock = new();
    private static bool _sqliteRuntimeInitialized;
    private readonly object _migrationMessagesLock = new();
    private readonly Dictionary<string, List<string>> _pendingMigrationMessages = new(StringComparer.OrdinalIgnoreCase);

    // Fresh CREATE TABLE statements are not enough here. Any persisted model change must
    // also update this manifest so existing ram.db files can be upgraded in place.
    private static readonly IReadOnlyList<TableSchemaDefinition> TableSchemas =
    [
        new TableSchemaDefinition(
            "intents",
            """
            CREATE TABLE IF NOT EXISTS intents (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                title TEXT NOT NULL,
                objective TEXT NOT NULL,
                notes TEXT NOT NULL,
                last_updated_utc TEXT NOT NULL
            );
            """,
            [
                new ColumnSchemaDefinition("id", "INTEGER PRIMARY KEY CHECK (id = 1)", null),
                new ColumnSchemaDefinition("title", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("objective", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("notes", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_updated_utc", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''")
            ]),
        new TableSchemaDefinition(
            "memory_summaries",
            """
            CREATE TABLE IF NOT EXISTS memory_summaries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_root TEXT NOT NULL,
                source_type TEXT NOT NULL,
                source_id TEXT NOT NULL,
                summary_text TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            """,
            [
                new ColumnSchemaDefinition("id", "INTEGER PRIMARY KEY AUTOINCREMENT", null),
                new ColumnSchemaDefinition("workspace_root", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("source_type", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("source_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("summary_text", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("created_utc", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''")
            ],
            [
                """
                CREATE INDEX IF NOT EXISTS idx_memory_summaries_workspace
                ON memory_summaries (workspace_root);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_memory_summaries_source
                ON memory_summaries (source_type, source_id);
                """
            ]),
        new TableSchemaDefinition(
            "artifacts",
            """
            CREATE TABLE IF NOT EXISTS artifacts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_root TEXT NOT NULL,
                intent_title TEXT NOT NULL,
                artifact_type TEXT NOT NULL,
                title TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                content TEXT NOT NULL,
                summary TEXT NOT NULL,
                data_category TEXT NOT NULL,
                retention_class TEXT NOT NULL,
                lifecycle_state TEXT NOT NULL,
                content_sha256 TEXT NOT NULL,
                content_length INTEGER NOT NULL,
                source_run_state_id TEXT NOT NULL,
                source_batch_id TEXT NOT NULL,
                source_work_item_id TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """,
            [
                new ColumnSchemaDefinition("id", "INTEGER PRIMARY KEY AUTOINCREMENT", null),
                new ColumnSchemaDefinition("workspace_root", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("intent_title", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("artifact_type", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("title", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("relative_path", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("content", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("summary", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("data_category", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("retention_class", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("lifecycle_state", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("content_sha256", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("content_length", "INTEGER NOT NULL", "INTEGER NOT NULL DEFAULT 0"),
                new ColumnSchemaDefinition("source_run_state_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("source_batch_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("source_work_item_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("created_utc", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("updated_utc", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''")
            ],
            [
                """
                CREATE INDEX IF NOT EXISTS idx_artifacts_workspace
                ON artifacts (workspace_root);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_artifacts_type
                ON artifacts (workspace_root, artifact_type);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_artifacts_title_path
                ON artifacts (workspace_root, title, relative_path);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_artifacts_category
                ON artifacts (workspace_root, data_category, retention_class);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_artifacts_source_run
                ON artifacts (workspace_root, source_run_state_id, source_work_item_id);
                """
            ]),
        new TableSchemaDefinition(
            "file_touch_records",
            """
            CREATE TABLE IF NOT EXISTS file_touch_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_root TEXT NOT NULL,
                run_state_id TEXT NOT NULL,
                plan_import_id TEXT NOT NULL,
                batch_id TEXT NOT NULL,
                work_item_id TEXT NOT NULL,
                work_item_title TEXT NOT NULL,
                file_path TEXT NOT NULL,
                operation_type TEXT NOT NULL,
                reason TEXT NOT NULL,
                source_action_name TEXT NOT NULL,
                artifact_type TEXT NOT NULL,
                is_productive_touch INTEGER NOT NULL,
                content_changed INTEGER NOT NULL,
                touch_order_index INTEGER NOT NULL,
                created_utc TEXT NOT NULL
            );
            """,
            [
                new ColumnSchemaDefinition("id", "INTEGER PRIMARY KEY AUTOINCREMENT", null),
                new ColumnSchemaDefinition("workspace_root", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("run_state_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("plan_import_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("batch_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("work_item_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("work_item_title", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("file_path", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("operation_type", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("reason", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("source_action_name", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("artifact_type", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("is_productive_touch", "INTEGER NOT NULL", "INTEGER NOT NULL DEFAULT 0"),
                new ColumnSchemaDefinition("content_changed", "INTEGER NOT NULL", "INTEGER NOT NULL DEFAULT 0"),
                new ColumnSchemaDefinition("touch_order_index", "INTEGER NOT NULL", "INTEGER NOT NULL DEFAULT 0"),
                new ColumnSchemaDefinition("created_utc", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''")
            ],
            [
                """
                CREATE INDEX IF NOT EXISTS idx_file_touch_run
                ON file_touch_records (workspace_root, run_state_id, touch_order_index);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_file_touch_file
                ON file_touch_records (workspace_root, file_path, created_utc);
                """
            ]),
        new TableSchemaDefinition(
            "taskboard_skip_records",
            """
            CREATE TABLE IF NOT EXISTS taskboard_skip_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_root TEXT NOT NULL,
                run_state_id TEXT NOT NULL,
                plan_import_id TEXT NOT NULL,
                batch_id TEXT NOT NULL,
                work_item_id TEXT NOT NULL,
                work_item_title TEXT NOT NULL,
                step_id TEXT NOT NULL,
                skip_family TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                reason_code TEXT NOT NULL,
                evidence_source TEXT NOT NULL,
                evidence_summary TEXT NOT NULL,
                used_file_touch_fast_path INTEGER NOT NULL,
                repeated_touches_avoided_count INTEGER NOT NULL,
                linked_files_json TEXT NOT NULL,
                linked_artifact_ids_json TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            """,
            [
                new ColumnSchemaDefinition("id", "INTEGER PRIMARY KEY AUTOINCREMENT", null),
                new ColumnSchemaDefinition("workspace_root", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("run_state_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("plan_import_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("batch_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("work_item_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("work_item_title", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("step_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("skip_family", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("tool_name", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("reason_code", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("evidence_source", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("evidence_summary", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("used_file_touch_fast_path", "INTEGER NOT NULL", "INTEGER NOT NULL DEFAULT 0"),
                new ColumnSchemaDefinition("repeated_touches_avoided_count", "INTEGER NOT NULL", "INTEGER NOT NULL DEFAULT 0"),
                new ColumnSchemaDefinition("linked_files_json", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT '[]'"),
                new ColumnSchemaDefinition("linked_artifact_ids_json", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT '[]'"),
                new ColumnSchemaDefinition("created_utc", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''")
            ],
            [
                """
                CREATE INDEX IF NOT EXISTS idx_taskboard_skip_run
                ON taskboard_skip_records (workspace_root, run_state_id, created_utc);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_taskboard_skip_work_item
                ON taskboard_skip_records (workspace_root, work_item_id, created_utc);
                """
            ]),
        new TableSchemaDefinition(
            "workspace_execution_state",
            """
            CREATE TABLE IF NOT EXISTS workspace_execution_state (
                workspace_root TEXT PRIMARY KEY,
                last_failure_tool_name TEXT NOT NULL,
                last_failure_outcome_type TEXT NOT NULL,
                last_failure_target_path TEXT NOT NULL,
                last_failure_summary TEXT NOT NULL,
                last_failure_data_json TEXT NOT NULL,
                last_failure_utc TEXT NOT NULL,
                last_success_tool_name TEXT NOT NULL,
                last_success_outcome_type TEXT NOT NULL,
                last_success_target_path TEXT NOT NULL,
                last_success_summary TEXT NOT NULL,
                last_success_data_json TEXT NOT NULL,
                last_success_utc TEXT NOT NULL,
                last_verification_plan_id TEXT NOT NULL,
                last_verified_patch_draft_id TEXT NOT NULL,
                last_verification_tool_name TEXT NOT NULL,
                last_verification_outcome_type TEXT NOT NULL,
                last_verification_target_path TEXT NOT NULL,
                last_verification_summary TEXT NOT NULL,
                last_verification_data_json TEXT NOT NULL,
                last_verification_utc TEXT NOT NULL,
                last_detected_build_system_type TEXT NOT NULL,
                last_selected_build_profile_type TEXT NOT NULL,
                last_selected_build_profile_target_path TEXT NOT NULL,
                last_selected_build_profile_json TEXT NOT NULL,
                last_configure_tool_name TEXT NOT NULL,
                last_build_tool_family TEXT NOT NULL,
                last_verification_family TEXT NOT NULL
            );
            """,
            [
                new ColumnSchemaDefinition("workspace_root", "TEXT PRIMARY KEY", null),
                new ColumnSchemaDefinition("last_failure_tool_name", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_failure_outcome_type", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_failure_target_path", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_failure_summary", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_failure_data_json", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_failure_utc", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_success_tool_name", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_success_outcome_type", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_success_target_path", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_success_summary", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_success_data_json", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_success_utc", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_verification_plan_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_verified_patch_draft_id", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_verification_tool_name", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_verification_outcome_type", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_verification_target_path", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_verification_summary", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_verification_data_json", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_verification_utc", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_detected_build_system_type", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_selected_build_profile_type", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_selected_build_profile_target_path", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_selected_build_profile_json", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_configure_tool_name", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_build_tool_family", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''"),
                new ColumnSchemaDefinition("last_verification_family", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''")
            ],
            [
                """
                CREATE INDEX IF NOT EXISTS idx_workspace_execution_failure
                ON workspace_execution_state (last_failure_utc);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_workspace_execution_success
                ON workspace_execution_state (last_success_utc);
                """,
                """
                CREATE INDEX IF NOT EXISTS idx_workspace_execution_verification
                ON workspace_execution_state (last_verification_utc);
                """
            ]),
        new TableSchemaDefinition(
            "ram_metadata",
            """
            CREATE TABLE IF NOT EXISTS ram_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """,
            [
                new ColumnSchemaDefinition("key", "TEXT PRIMARY KEY", null),
                new ColumnSchemaDefinition("value", "TEXT NOT NULL", "TEXT NOT NULL DEFAULT ''")
            ])
    ];

    public string GetDatabasePath(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        var ramDir = Path.Combine(workspaceRoot, ".ram");
        Directory.CreateDirectory(ramDir);

        return Path.Combine(ramDir, "ram.db");
    }

    public void EnsureDatabase(string workspaceRoot)
    {
        EnsureSqliteRuntime();
        var dbPath = GetDatabasePath(workspaceRoot);
        var migrationMessages = new List<string>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        foreach (var schema in TableSchemas)
            EnsureTableSchema(connection, workspaceRoot, schema, migrationMessages);

        EnsureSchemaVersionMetadata(connection, workspaceRoot, migrationMessages);
        StoreMigrationMessages(workspaceRoot, migrationMessages);
    }

    private static void EnsureSqliteRuntime()
    {
        if (_sqliteRuntimeInitialized)
            return;

        lock (SqliteRuntimeLock)
        {
            if (_sqliteRuntimeInitialized)
                return;

            try
            {
                TryLoadBundledSqliteNativeLibrary();
                Batteries_V2.Init();
                _sqliteRuntimeInitialized = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("SQLite runtime initialization failed for RAM persistence.", ex);
            }
        }
    }

    private static void TryLoadBundledSqliteNativeLibrary()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(RamDbService).Assembly.Location);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            return;

        var relativeNativePath = GetBundledSqliteNativePath();
        if (string.IsNullOrWhiteSpace(relativeNativePath))
            return;

        var nativeLibraryPath = Path.Combine(assemblyDirectory, relativeNativePath);
        if (!File.Exists(nativeLibraryPath))
            return;

        try
        {
            NativeLibrary.Load(nativeLibraryPath);
        }
        catch
        {
        }
    }

    private static string GetBundledSqliteNativePath()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => Path.Combine("runtimes", "win-x64", "native", "e_sqlite3.dll"),
                Architecture.X86 => Path.Combine("runtimes", "win-x86", "native", "e_sqlite3.dll"),
                Architecture.Arm64 => Path.Combine("runtimes", "win-arm64", "native", "e_sqlite3.dll"),
                Architecture.Arm => Path.Combine("runtimes", "win-arm", "native", "e_sqlite3.dll"),
                _ => ""
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => Path.Combine("runtimes", "linux-x64", "native", "libe_sqlite3.so"),
                Architecture.X86 => Path.Combine("runtimes", "linux-x86", "native", "libe_sqlite3.so"),
                Architecture.Arm64 => Path.Combine("runtimes", "linux-arm64", "native", "libe_sqlite3.so"),
                Architecture.Arm => Path.Combine("runtimes", "linux-arm", "native", "libe_sqlite3.so"),
                _ => ""
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => Path.Combine("runtimes", "osx-x64", "native", "libe_sqlite3.dylib"),
                Architecture.Arm64 => Path.Combine("runtimes", "osx-arm64", "native", "libe_sqlite3.dylib"),
                _ => ""
            };
        }

        return "";
    }

    public IReadOnlyList<string> DrainMigrationMessages(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return [];

        lock (_migrationMessagesLock)
        {
            if (!_pendingMigrationMessages.TryGetValue(workspaceRoot, out var messages) || messages.Count == 0)
                return [];

            _pendingMigrationMessages.Remove(workspaceRoot);
            return messages.ToList();
        }
    }

    public IntentRecord LoadCurrentIntent(string workspaceRoot)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT title, objective, notes, last_updated_utc
        FROM intents
        WHERE id = 1;
        """;

        using var reader = command.ExecuteReader();

        if (!reader.Read())
            return new IntentRecord();

        return new IntentRecord
        {
            Title = reader.IsDBNull(0) ? "" : reader.GetString(0),
            Objective = reader.IsDBNull(1) ? "" : reader.GetString(1),
            Notes = reader.IsDBNull(2) ? "" : reader.GetString(2),
            LastUpdatedUtc = reader.IsDBNull(3) ? "" : reader.GetString(3)
        };
    }

    public void SaveCurrentIntent(string workspaceRoot, IntentRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        record.LastUpdatedUtc = DateTime.UtcNow.ToString("O");

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        INSERT INTO intents (id, title, objective, notes, last_updated_utc)
        VALUES (1, $title, $objective, $notes, $lastUpdatedUtc)
        ON CONFLICT(id) DO UPDATE SET
            title = excluded.title,
            objective = excluded.objective,
            notes = excluded.notes,
            last_updated_utc = excluded.last_updated_utc;
        """;

        command.Parameters.AddWithValue("$title", record.Title ?? "");
        command.Parameters.AddWithValue("$objective", record.Objective ?? "");
        command.Parameters.AddWithValue("$notes", record.Notes ?? "");
        command.Parameters.AddWithValue("$lastUpdatedUtc", record.LastUpdatedUtc ?? "");

        command.ExecuteNonQuery();
    }

    public void AddMemorySummary(string workspaceRoot, MemorySummaryRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        record.CreatedUtc = DateTime.UtcNow.ToString("O");
        record.WorkspaceRoot = workspaceRoot;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        INSERT INTO memory_summaries (
            workspace_root,
            source_type,
            source_id,
            summary_text,
            created_utc
        )
        VALUES (
            $workspaceRoot,
            $sourceType,
            $sourceId,
            $summaryText,
            $createdUtc
        );
        """;

        command.Parameters.AddWithValue("$workspaceRoot", record.WorkspaceRoot ?? "");
        command.Parameters.AddWithValue("$sourceType", record.SourceType ?? "");
        command.Parameters.AddWithValue("$sourceId", record.SourceId ?? "");
        command.Parameters.AddWithValue("$summaryText", record.SummaryText ?? "");
        command.Parameters.AddWithValue("$createdUtc", record.CreatedUtc ?? "");

        command.ExecuteNonQuery();
    }

    public List<MemorySummaryRecord> LoadRecentMemorySummaries(string workspaceRoot, int maxCount = 10)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        var results = new List<MemorySummaryRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id, workspace_root, source_type, source_id, summary_text, created_utc
        FROM memory_summaries
        WHERE workspace_root = $workspaceRoot
        ORDER BY id DESC
        LIMIT $maxCount;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$maxCount", maxCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new MemorySummaryRecord
            {
                Id = reader.GetInt64(0),
                WorkspaceRoot = reader.IsDBNull(1) ? "" : reader.GetString(1),
                SourceType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                SourceId = reader.IsDBNull(3) ? "" : reader.GetString(3),
                SummaryText = reader.IsDBNull(4) ? "" : reader.GetString(4),
                CreatedUtc = reader.IsDBNull(5) ? "" : reader.GetString(5)
            });
        }

        return results;
    }

    public List<MemorySummaryRecord> LoadMemorySummariesSince(string workspaceRoot, string sinceUtc, int maxCount = 5000)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        var results = new List<MemorySummaryRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id, workspace_root, source_type, source_id, summary_text, created_utc
        FROM memory_summaries
        WHERE workspace_root = $workspaceRoot
          AND created_utc >= $sinceUtc
        ORDER BY created_utc ASC, id ASC
        LIMIT $maxCount;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$sinceUtc", sinceUtc ?? "");
        command.Parameters.AddWithValue("$maxCount", maxCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new MemorySummaryRecord
            {
                Id = reader.GetInt64(0),
                WorkspaceRoot = reader.IsDBNull(1) ? "" : reader.GetString(1),
                SourceType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                SourceId = reader.IsDBNull(3) ? "" : reader.GetString(3),
                SummaryText = reader.IsDBNull(4) ? "" : reader.GetString(4),
                CreatedUtc = reader.IsDBNull(5) ? "" : reader.GetString(5)
            });
        }

        return results;
    }

    public ArtifactRecord SaveArtifact(string workspaceRoot, ArtifactRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));

        EnsureDatabase(workspaceRoot);
        PrepareArtifactRecord(record);

        var dbPath = GetDatabasePath(workspaceRoot);
        var nowUtc = DateTime.UtcNow.ToString("O");

        record.WorkspaceRoot = workspaceRoot;
        record.CreatedUtc = nowUtc;
        record.UpdatedUtc = nowUtc;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        INSERT INTO artifacts (
            workspace_root,
            intent_title,
            artifact_type,
            title,
            relative_path,
            content,
            summary,
            data_category,
            retention_class,
            lifecycle_state,
            content_sha256,
            content_length,
            source_run_state_id,
            source_batch_id,
            source_work_item_id,
            created_utc,
            updated_utc
        )
        VALUES (
            $workspaceRoot,
            $intentTitle,
            $artifactType,
            $title,
            $relativePath,
            $content,
            $summary,
            $dataCategory,
            $retentionClass,
            $lifecycleState,
            $contentSha256,
            $contentLength,
            $sourceRunStateId,
            $sourceBatchId,
            $sourceWorkItemId,
            $createdUtc,
            $updatedUtc
        );

        SELECT last_insert_rowid();
        """;

        AddArtifactParameters(command, record);
        command.Parameters.AddWithValue("$createdUtc", record.CreatedUtc ?? "");
        command.Parameters.AddWithValue("$updatedUtc", record.UpdatedUtc ?? "");

        record.Id = (long)(command.ExecuteScalar() ?? 0L);
        return record;
    }

    public void UpdateArtifact(string workspaceRoot, ArtifactRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));

        if (record.Id <= 0)
            throw new ArgumentException("Artifact id is required.", nameof(record));

        EnsureDatabase(workspaceRoot);
        PrepareArtifactRecord(record);

        var dbPath = GetDatabasePath(workspaceRoot);
        record.WorkspaceRoot = workspaceRoot;
        record.UpdatedUtc = DateTime.UtcNow.ToString("O");

        if (string.IsNullOrWhiteSpace(record.CreatedUtc))
            record.CreatedUtc = record.UpdatedUtc;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        UPDATE artifacts
        SET workspace_root = $workspaceRoot,
            intent_title = $intentTitle,
            artifact_type = $artifactType,
            title = $title,
            relative_path = $relativePath,
            content = $content,
            summary = $summary,
            data_category = $dataCategory,
            retention_class = $retentionClass,
            lifecycle_state = $lifecycleState,
            content_sha256 = $contentSha256,
            content_length = $contentLength,
            source_run_state_id = $sourceRunStateId,
            source_batch_id = $sourceBatchId,
            source_work_item_id = $sourceWorkItemId,
            updated_utc = $updatedUtc
        WHERE id = $id;
        """;

        AddArtifactParameters(command, record);
        command.Parameters.AddWithValue("$updatedUtc", record.UpdatedUtc ?? "");
        command.Parameters.AddWithValue("$id", record.Id);

        command.ExecuteNonQuery();
    }

    public ArtifactRecord? LoadArtifactById(string workspaceRoot, long id)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               intent_title,
               artifact_type,
               title,
               relative_path,
               content,
               summary,
               data_category,
               retention_class,
               lifecycle_state,
               content_sha256,
               content_length,
               source_run_state_id,
               source_batch_id,
               source_work_item_id,
               created_utc,
               updated_utc
        FROM artifacts
        WHERE workspace_root = $workspaceRoot
          AND id = $id;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadArtifact(reader) : null;
    }

    public List<ArtifactRecord> LoadLatestArtifacts(string workspaceRoot, int maxCount = 10)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        var results = new List<ArtifactRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               intent_title,
               artifact_type,
               title,
               relative_path,
               content,
               summary,
               data_category,
               retention_class,
               lifecycle_state,
               content_sha256,
               content_length,
               source_run_state_id,
               source_batch_id,
               source_work_item_id,
               created_utc,
               updated_utc
        FROM artifacts
        WHERE workspace_root = $workspaceRoot
        ORDER BY updated_utc DESC, id DESC
        LIMIT $maxCount;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$maxCount", maxCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadArtifact(reader));
        }

        return results;
    }

    public List<ArtifactRecord> LoadArtifactsSince(string workspaceRoot, string sinceUtc, int maxCount = 400)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        var results = new List<ArtifactRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               intent_title,
               artifact_type,
               title,
               relative_path,
               content,
               summary,
               data_category,
               retention_class,
               lifecycle_state,
               content_sha256,
               content_length,
               source_run_state_id,
               source_batch_id,
               source_work_item_id,
               created_utc,
               updated_utc
        FROM artifacts
        WHERE workspace_root = $workspaceRoot
          AND updated_utc >= $sinceUtc
        ORDER BY updated_utc DESC, id DESC
        LIMIT $maxCount;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$sinceUtc", sinceUtc ?? "");
        command.Parameters.AddWithValue("$maxCount", maxCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadArtifact(reader));
        }

        return results;
    }

    public ArtifactRecord? LoadLatestArtifactByRelativePath(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path is required.", nameof(relativePath));

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               intent_title,
               artifact_type,
               title,
               relative_path,
               content,
               summary,
               data_category,
               retention_class,
               lifecycle_state,
               content_sha256,
               content_length,
               source_run_state_id,
               source_batch_id,
               source_work_item_id,
               created_utc,
               updated_utc
        FROM artifacts
        WHERE workspace_root = $workspaceRoot
          AND relative_path = $relativePath
        ORDER BY updated_utc DESC, id DESC
        LIMIT 1;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$relativePath", relativePath);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadArtifact(reader) : null;
    }

    public ArtifactRecord? LoadLatestArtifactByType(string workspaceRoot, string artifactType)
    {
        if (string.IsNullOrWhiteSpace(artifactType))
            throw new ArgumentException("Artifact type is required.", nameof(artifactType));

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               intent_title,
               artifact_type,
               title,
               relative_path,
               content,
               summary,
               data_category,
               retention_class,
               lifecycle_state,
               content_sha256,
               content_length,
               source_run_state_id,
               source_batch_id,
               source_work_item_id,
               created_utc,
               updated_utc
        FROM artifacts
        WHERE workspace_root = $workspaceRoot
          AND artifact_type = $artifactType
        ORDER BY updated_utc DESC, id DESC
        LIMIT 1;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$artifactType", artifactType);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadArtifact(reader) : null;
    }

    public ArtifactRecord? LoadLatestArtifactByTypeAndIntentTitle(string workspaceRoot, string artifactType, string intentTitle)
    {
        if (string.IsNullOrWhiteSpace(artifactType))
            throw new ArgumentException("Artifact type is required.", nameof(artifactType));

        if (string.IsNullOrWhiteSpace(intentTitle))
            throw new ArgumentException("Intent title is required.", nameof(intentTitle));

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               intent_title,
               artifact_type,
               title,
               relative_path,
               content,
               summary,
               data_category,
               retention_class,
               lifecycle_state,
               content_sha256,
               content_length,
               source_run_state_id,
               source_batch_id,
               source_work_item_id,
               created_utc,
               updated_utc
        FROM artifacts
        WHERE workspace_root = $workspaceRoot
          AND artifact_type = $artifactType
          AND intent_title = $intentTitle
        ORDER BY updated_utc DESC, id DESC
        LIMIT 1;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$artifactType", artifactType);
        command.Parameters.AddWithValue("$intentTitle", intentTitle);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadArtifact(reader) : null;
    }

    public List<ArtifactRecord> LoadArtifactsByType(string workspaceRoot, string artifactType, int maxCount = 40)
    {
        if (string.IsNullOrWhiteSpace(artifactType))
            throw new ArgumentException("Artifact type is required.", nameof(artifactType));

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        var results = new List<ArtifactRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               intent_title,
               artifact_type,
               title,
               relative_path,
               content,
               summary,
               data_category,
               retention_class,
               lifecycle_state,
               content_sha256,
               content_length,
               source_run_state_id,
               source_batch_id,
               source_work_item_id,
               created_utc,
               updated_utc
        FROM artifacts
        WHERE workspace_root = $workspaceRoot
          AND artifact_type = $artifactType
        ORDER BY updated_utc DESC, id DESC
        LIMIT $maxCount;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$artifactType", artifactType);
        command.Parameters.AddWithValue("$maxCount", maxCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(ReadArtifact(reader));

        return results;
    }

    public WorkspaceExecutionStateRecord LoadExecutionState(string workspaceRoot)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT workspace_root,
               last_failure_tool_name,
               last_failure_outcome_type,
               last_failure_target_path,
               last_failure_summary,
               last_failure_data_json,
               last_failure_utc,
               last_success_tool_name,
               last_success_outcome_type,
               last_success_target_path,
               last_success_summary,
               last_success_data_json,
               last_success_utc,
               last_verification_plan_id,
               last_verified_patch_draft_id,
               last_verification_tool_name,
               last_verification_outcome_type,
               last_verification_target_path,
               last_verification_summary,
               last_verification_data_json,
               last_verification_utc,
               last_detected_build_system_type,
               last_selected_build_profile_type,
               last_selected_build_profile_target_path,
               last_selected_build_profile_json,
               last_configure_tool_name,
               last_build_tool_family,
               last_verification_family
        FROM workspace_execution_state
        WHERE workspace_root = $workspaceRoot;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadExecutionState(reader)
            : new WorkspaceExecutionStateRecord { WorkspaceRoot = workspaceRoot };
    }

    public RamFileTouchRecord AddFileTouchRecord(string workspaceRoot, RamFileTouchRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        record.WorkspaceRoot = workspaceRoot;
        record.CreatedUtc = string.IsNullOrWhiteSpace(record.CreatedUtc)
            ? DateTime.UtcNow.ToString("O")
            : record.CreatedUtc;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var orderCommand = connection.CreateCommand();
        orderCommand.CommandText =
        """
        SELECT COALESCE(MAX(touch_order_index), 0)
        FROM file_touch_records
        WHERE workspace_root = $workspaceRoot
          AND run_state_id = $runStateId;
        """;
        orderCommand.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        orderCommand.Parameters.AddWithValue("$runStateId", record.RunStateId ?? "");
        var currentMax = Convert.ToInt32(orderCommand.ExecuteScalar() ?? 0);
        record.TouchOrderIndex = record.TouchOrderIndex > 0 ? record.TouchOrderIndex : currentMax + 1;

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        INSERT INTO file_touch_records (
            workspace_root,
            run_state_id,
            plan_import_id,
            batch_id,
            work_item_id,
            work_item_title,
            file_path,
            operation_type,
            reason,
            source_action_name,
            artifact_type,
            is_productive_touch,
            content_changed,
            touch_order_index,
            created_utc
        )
        VALUES (
            $workspaceRoot,
            $runStateId,
            $planImportId,
            $batchId,
            $workItemId,
            $workItemTitle,
            $filePath,
            $operationType,
            $reason,
            $sourceActionName,
            $artifactType,
            $isProductiveTouch,
            $contentChanged,
            $touchOrderIndex,
            $createdUtc
        );

        SELECT last_insert_rowid();
        """;
        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$runStateId", record.RunStateId ?? "");
        command.Parameters.AddWithValue("$planImportId", record.PlanImportId ?? "");
        command.Parameters.AddWithValue("$batchId", record.BatchId ?? "");
        command.Parameters.AddWithValue("$workItemId", record.WorkItemId ?? "");
        command.Parameters.AddWithValue("$workItemTitle", record.WorkItemTitle ?? "");
        command.Parameters.AddWithValue("$filePath", record.FilePath ?? "");
        command.Parameters.AddWithValue("$operationType", record.OperationType ?? "");
        command.Parameters.AddWithValue("$reason", record.Reason ?? "");
        command.Parameters.AddWithValue("$sourceActionName", record.SourceActionName ?? "");
        command.Parameters.AddWithValue("$artifactType", record.ArtifactType ?? "");
        command.Parameters.AddWithValue("$isProductiveTouch", record.IsProductiveTouch ? 1 : 0);
        command.Parameters.AddWithValue("$contentChanged", record.ContentChanged ? 1 : 0);
        command.Parameters.AddWithValue("$touchOrderIndex", record.TouchOrderIndex);
        command.Parameters.AddWithValue("$createdUtc", record.CreatedUtc ?? "");
        record.Id = (long)(command.ExecuteScalar() ?? 0L);
        return record;
    }

    public List<RamFileTouchRecord> LoadFileTouchRecordsForRun(string workspaceRoot, string runStateId, int maxCount = 4000)
    {
        if (string.IsNullOrWhiteSpace(runStateId))
            return [];

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        var results = new List<RamFileTouchRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               run_state_id,
               plan_import_id,
               batch_id,
               work_item_id,
               work_item_title,
               file_path,
               operation_type,
               reason,
               source_action_name,
               artifact_type,
               is_productive_touch,
               content_changed,
               touch_order_index,
               created_utc
        FROM file_touch_records
        WHERE workspace_root = $workspaceRoot
          AND run_state_id = $runStateId
        ORDER BY touch_order_index ASC, id ASC
        LIMIT $maxCount;
        """;
        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$runStateId", runStateId);
        command.Parameters.AddWithValue("$maxCount", maxCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(ReadFileTouchRecord(reader));

        return results;
    }

    public List<RamFileTouchRecord> LoadFileTouchRecordsSince(string workspaceRoot, string sinceUtc, int maxCount = 12000)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        var results = new List<RamFileTouchRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               run_state_id,
               plan_import_id,
               batch_id,
               work_item_id,
               work_item_title,
               file_path,
               operation_type,
               reason,
               source_action_name,
               artifact_type,
               is_productive_touch,
               content_changed,
               touch_order_index,
               created_utc
        FROM file_touch_records
        WHERE workspace_root = $workspaceRoot
          AND created_utc >= $sinceUtc
        ORDER BY created_utc ASC, id ASC
        LIMIT $maxCount;
        """;
        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$sinceUtc", sinceUtc ?? "");
        command.Parameters.AddWithValue("$maxCount", maxCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(ReadFileTouchRecord(reader));

        return results;
    }

    public TaskboardSkipDecisionRecord AddTaskboardSkipRecord(string workspaceRoot, TaskboardSkipDecisionRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        record.WorkspaceRoot = workspaceRoot;
        record.CreatedUtc = string.IsNullOrWhiteSpace(record.CreatedUtc)
            ? DateTime.UtcNow.ToString("O")
            : record.CreatedUtc;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        INSERT INTO taskboard_skip_records (
            workspace_root,
            run_state_id,
            plan_import_id,
            batch_id,
            work_item_id,
            work_item_title,
            step_id,
            skip_family,
            tool_name,
            reason_code,
            evidence_source,
            evidence_summary,
            used_file_touch_fast_path,
            repeated_touches_avoided_count,
            linked_files_json,
            linked_artifact_ids_json,
            created_utc
        )
        VALUES (
            $workspaceRoot,
            $runStateId,
            $planImportId,
            $batchId,
            $workItemId,
            $workItemTitle,
            $stepId,
            $skipFamily,
            $toolName,
            $reasonCode,
            $evidenceSource,
            $evidenceSummary,
            $usedFileTouchFastPath,
            $repeatedTouchesAvoidedCount,
            $linkedFilesJson,
            $linkedArtifactIdsJson,
            $createdUtc
        );

        SELECT last_insert_rowid();
        """;
        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$runStateId", record.RunStateId ?? "");
        command.Parameters.AddWithValue("$planImportId", record.PlanImportId ?? "");
        command.Parameters.AddWithValue("$batchId", record.BatchId ?? "");
        command.Parameters.AddWithValue("$workItemId", record.WorkItemId ?? "");
        command.Parameters.AddWithValue("$workItemTitle", record.WorkItemTitle ?? "");
        command.Parameters.AddWithValue("$stepId", record.StepId ?? "");
        command.Parameters.AddWithValue("$skipFamily", record.SkipFamily ?? "");
        command.Parameters.AddWithValue("$toolName", record.ToolName ?? "");
        command.Parameters.AddWithValue("$reasonCode", record.ReasonCode ?? "");
        command.Parameters.AddWithValue("$evidenceSource", record.EvidenceSource ?? "");
        command.Parameters.AddWithValue("$evidenceSummary", record.EvidenceSummary ?? "");
        command.Parameters.AddWithValue("$usedFileTouchFastPath", record.UsedFileTouchFastPath ? 1 : 0);
        command.Parameters.AddWithValue("$repeatedTouchesAvoidedCount", record.RepeatedTouchesAvoidedCount);
        command.Parameters.AddWithValue("$linkedFilesJson", SerializeJson(record.LinkedFilePaths ?? []));
        command.Parameters.AddWithValue("$linkedArtifactIdsJson", SerializeJson(record.LinkedArtifactIds ?? []));
        command.Parameters.AddWithValue("$createdUtc", record.CreatedUtc ?? "");
        record.Id = (long)(command.ExecuteScalar() ?? 0L);
        return record;
    }

    public List<TaskboardSkipDecisionRecord> LoadTaskboardSkipRecordsForRun(string workspaceRoot, string runStateId, int maxCount = 4000)
    {
        if (string.IsNullOrWhiteSpace(runStateId))
            return [];

        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        var results = new List<TaskboardSkipDecisionRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               run_state_id,
               plan_import_id,
               batch_id,
               work_item_id,
               work_item_title,
               step_id,
               skip_family,
               tool_name,
               reason_code,
               evidence_source,
               evidence_summary,
               used_file_touch_fast_path,
               repeated_touches_avoided_count,
               linked_files_json,
               linked_artifact_ids_json,
               created_utc
        FROM taskboard_skip_records
        WHERE workspace_root = $workspaceRoot
          AND run_state_id = $runStateId
        ORDER BY created_utc ASC, id ASC
        LIMIT $maxCount;
        """;
        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$runStateId", runStateId);
        command.Parameters.AddWithValue("$maxCount", maxCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(ReadTaskboardSkipRecord(reader));

        return results;
    }

    public List<TaskboardSkipDecisionRecord> LoadTaskboardSkipRecordsSince(string workspaceRoot, string sinceUtc, int maxCount = 12000)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);
        var results = new List<TaskboardSkipDecisionRecord>();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT id,
               workspace_root,
               run_state_id,
               plan_import_id,
               batch_id,
               work_item_id,
               work_item_title,
               step_id,
               skip_family,
               tool_name,
               reason_code,
               evidence_source,
               evidence_summary,
               used_file_touch_fast_path,
               repeated_touches_avoided_count,
               linked_files_json,
               linked_artifact_ids_json,
               created_utc
        FROM taskboard_skip_records
        WHERE workspace_root = $workspaceRoot
          AND created_utc >= $sinceUtc
        ORDER BY created_utc ASC, id ASC
        LIMIT $maxCount;
        """;
        command.Parameters.AddWithValue("$workspaceRoot", workspaceRoot);
        command.Parameters.AddWithValue("$sinceUtc", sinceUtc ?? "");
        command.Parameters.AddWithValue("$maxCount", maxCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(ReadTaskboardSkipRecord(reader));

        return results;
    }

    public void SaveExecutionFailure(
        string workspaceRoot,
        string toolName,
        string outcomeType,
        string targetPath,
        string summary,
        string dataJson)
    {
        var record = LoadExecutionState(workspaceRoot);
        record.WorkspaceRoot = workspaceRoot;
        record.LastFailureToolName = toolName ?? "";
        record.LastFailureOutcomeType = outcomeType ?? "";
        record.LastFailureTargetPath = targetPath ?? "";
        record.LastFailureSummary = summary ?? "";
        record.LastFailureDataJson = dataJson ?? "";
        record.LastFailureUtc = DateTime.UtcNow.ToString("O");
        SaveExecutionState(workspaceRoot, record);
    }

    public void SaveExecutionSuccess(
        string workspaceRoot,
        string toolName,
        string outcomeType,
        string targetPath,
        string summary,
        string dataJson)
    {
        var record = LoadExecutionState(workspaceRoot);
        record.WorkspaceRoot = workspaceRoot;
        record.LastSuccessToolName = toolName ?? "";
        record.LastSuccessOutcomeType = outcomeType ?? "";
        record.LastSuccessTargetPath = targetPath ?? "";
        record.LastSuccessSummary = summary ?? "";
        record.LastSuccessDataJson = dataJson ?? "";
        record.LastSuccessUtc = DateTime.UtcNow.ToString("O");
        SaveExecutionState(workspaceRoot, record);
    }

    public void SaveVerificationOutcome(
        string workspaceRoot,
        string planId,
        string patchDraftId,
        string toolName,
        string outcomeType,
        string targetPath,
        string summary,
        string dataJson)
    {
        var record = LoadExecutionState(workspaceRoot);
        record.WorkspaceRoot = workspaceRoot;
        record.LastVerificationPlanId = planId ?? "";
        record.LastVerifiedPatchDraftId = patchDraftId ?? "";
        record.LastVerificationToolName = toolName ?? "";
        record.LastVerificationOutcomeType = outcomeType ?? "";
        record.LastVerificationTargetPath = targetPath ?? "";
        record.LastVerificationSummary = summary ?? "";
        record.LastVerificationDataJson = dataJson ?? "";
        record.LastVerificationUtc = DateTime.UtcNow.ToString("O");
        SaveExecutionState(workspaceRoot, record);
    }

    public void SaveBuildProfileState(
        string workspaceRoot,
        string detectedBuildSystemType,
        string selectedBuildProfileType,
        string selectedBuildProfileTargetPath,
        string selectedBuildProfileJson,
        string configureToolName,
        string buildToolFamily,
        string verificationFamily)
    {
        var record = LoadExecutionState(workspaceRoot);
        record.WorkspaceRoot = workspaceRoot;

        if (!string.IsNullOrWhiteSpace(detectedBuildSystemType))
            record.LastDetectedBuildSystemType = detectedBuildSystemType;

        if (!string.IsNullOrWhiteSpace(selectedBuildProfileType))
            record.LastSelectedBuildProfileType = selectedBuildProfileType;

        if (!string.IsNullOrWhiteSpace(selectedBuildProfileTargetPath))
            record.LastSelectedBuildProfileTargetPath = selectedBuildProfileTargetPath;

        if (!string.IsNullOrWhiteSpace(selectedBuildProfileJson))
            record.LastSelectedBuildProfileJson = selectedBuildProfileJson;

        if (!string.IsNullOrWhiteSpace(configureToolName))
            record.LastConfigureToolName = configureToolName;

        if (!string.IsNullOrWhiteSpace(buildToolFamily))
            record.LastBuildToolFamily = buildToolFamily;

        if (!string.IsNullOrWhiteSpace(verificationFamily))
            record.LastVerificationFamily = verificationFamily;

        SaveExecutionState(workspaceRoot, record);
    }

    private static void AddArtifactParameters(SqliteCommand command, ArtifactRecord record)
    {
        command.Parameters.AddWithValue("$workspaceRoot", record.WorkspaceRoot ?? "");
        command.Parameters.AddWithValue("$intentTitle", record.IntentTitle ?? "");
        command.Parameters.AddWithValue("$artifactType", record.ArtifactType ?? "");
        command.Parameters.AddWithValue("$title", record.Title ?? "");
        command.Parameters.AddWithValue("$relativePath", record.RelativePath ?? "");
        command.Parameters.AddWithValue("$content", record.Content ?? "");
        command.Parameters.AddWithValue("$summary", record.Summary ?? "");
        command.Parameters.AddWithValue("$dataCategory", record.DataCategory ?? "");
        command.Parameters.AddWithValue("$retentionClass", record.RetentionClass ?? "");
        command.Parameters.AddWithValue("$lifecycleState", record.LifecycleState ?? "");
        command.Parameters.AddWithValue("$contentSha256", record.ContentSha256 ?? "");
        command.Parameters.AddWithValue("$contentLength", record.ContentLengthBytes);
        command.Parameters.AddWithValue("$sourceRunStateId", record.SourceRunStateId ?? "");
        command.Parameters.AddWithValue("$sourceBatchId", record.SourceBatchId ?? "");
        command.Parameters.AddWithValue("$sourceWorkItemId", record.SourceWorkItemId ?? "");
    }

    private static ArtifactRecord ReadArtifact(SqliteDataReader reader)
    {
        return new ArtifactRecord
        {
            Id = reader.GetInt64(0),
            WorkspaceRoot = reader.IsDBNull(1) ? "" : reader.GetString(1),
            IntentTitle = reader.IsDBNull(2) ? "" : reader.GetString(2),
            ArtifactType = reader.IsDBNull(3) ? "" : reader.GetString(3),
            Title = reader.IsDBNull(4) ? "" : reader.GetString(4),
            RelativePath = reader.IsDBNull(5) ? "" : reader.GetString(5),
            Content = reader.IsDBNull(6) ? "" : reader.GetString(6),
            Summary = reader.IsDBNull(7) ? "" : reader.GetString(7),
            DataCategory = reader.IsDBNull(8) ? "" : reader.GetString(8),
            RetentionClass = reader.IsDBNull(9) ? "" : reader.GetString(9),
            LifecycleState = reader.IsDBNull(10) ? "" : reader.GetString(10),
            ContentSha256 = reader.IsDBNull(11) ? "" : reader.GetString(11),
            ContentLengthBytes = reader.IsDBNull(12) ? 0L : reader.GetInt64(12),
            SourceRunStateId = reader.IsDBNull(13) ? "" : reader.GetString(13),
            SourceBatchId = reader.IsDBNull(14) ? "" : reader.GetString(14),
            SourceWorkItemId = reader.IsDBNull(15) ? "" : reader.GetString(15),
            CreatedUtc = reader.IsDBNull(16) ? "" : reader.GetString(16),
            UpdatedUtc = reader.IsDBNull(17) ? "" : reader.GetString(17)
        };
    }

    private static RamFileTouchRecord ReadFileTouchRecord(SqliteDataReader reader)
    {
        return new RamFileTouchRecord
        {
            Id = reader.GetInt64(0),
            WorkspaceRoot = reader.IsDBNull(1) ? "" : reader.GetString(1),
            RunStateId = reader.IsDBNull(2) ? "" : reader.GetString(2),
            PlanImportId = reader.IsDBNull(3) ? "" : reader.GetString(3),
            BatchId = reader.IsDBNull(4) ? "" : reader.GetString(4),
            WorkItemId = reader.IsDBNull(5) ? "" : reader.GetString(5),
            WorkItemTitle = reader.IsDBNull(6) ? "" : reader.GetString(6),
            FilePath = reader.IsDBNull(7) ? "" : reader.GetString(7),
            OperationType = reader.IsDBNull(8) ? "" : reader.GetString(8),
            Reason = reader.IsDBNull(9) ? "" : reader.GetString(9),
            SourceActionName = reader.IsDBNull(10) ? "" : reader.GetString(10),
            ArtifactType = reader.IsDBNull(11) ? "" : reader.GetString(11),
            IsProductiveTouch = !reader.IsDBNull(12) && reader.GetInt64(12) != 0,
            ContentChanged = !reader.IsDBNull(13) && reader.GetInt64(13) != 0,
            TouchOrderIndex = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
            CreatedUtc = reader.IsDBNull(15) ? "" : reader.GetString(15)
        };
    }

    private static TaskboardSkipDecisionRecord ReadTaskboardSkipRecord(SqliteDataReader reader)
    {
        return new TaskboardSkipDecisionRecord
        {
            Id = reader.GetInt64(0),
            WorkspaceRoot = reader.IsDBNull(1) ? "" : reader.GetString(1),
            RunStateId = reader.IsDBNull(2) ? "" : reader.GetString(2),
            PlanImportId = reader.IsDBNull(3) ? "" : reader.GetString(3),
            BatchId = reader.IsDBNull(4) ? "" : reader.GetString(4),
            WorkItemId = reader.IsDBNull(5) ? "" : reader.GetString(5),
            WorkItemTitle = reader.IsDBNull(6) ? "" : reader.GetString(6),
            StepId = reader.IsDBNull(7) ? "" : reader.GetString(7),
            SkipFamily = reader.IsDBNull(8) ? "" : reader.GetString(8),
            ToolName = reader.IsDBNull(9) ? "" : reader.GetString(9),
            ReasonCode = reader.IsDBNull(10) ? "" : reader.GetString(10),
            EvidenceSource = reader.IsDBNull(11) ? "" : reader.GetString(11),
            EvidenceSummary = reader.IsDBNull(12) ? "" : reader.GetString(12),
            UsedFileTouchFastPath = !reader.IsDBNull(13) && reader.GetInt64(13) != 0,
            RepeatedTouchesAvoidedCount = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
            LinkedFilePaths = DeserializeJson<List<string>>(reader.IsDBNull(15) ? "[]" : reader.GetString(15)) ?? [],
            LinkedArtifactIds = DeserializeJson<List<long>>(reader.IsDBNull(16) ? "[]" : reader.GetString(16)) ?? [],
            CreatedUtc = reader.IsDBNull(17) ? "" : reader.GetString(17)
        };
    }

    private static void PrepareArtifactRecord(ArtifactRecord record)
    {
        record.RelativePath = (record.RelativePath ?? "").Replace('\\', '/');
        record.DataCategory = DataDisciplineService.ResolveArtifactDataCategory(record);
        record.RetentionClass = DataDisciplineService.ResolveArtifactRetentionClass(record);
        record.LifecycleState = DataDisciplineService.ResolveArtifactLifecycleState(record);
        record.ContentSha256 = ComputeContentSha256(record.Content ?? "");
        record.ContentLengthBytes = Encoding.UTF8.GetByteCount(record.Content ?? "");
    }

    private static string ComputeContentSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? "");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SerializeJson<T>(T value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static T? DeserializeJson<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    private void SaveExecutionState(string workspaceRoot, WorkspaceExecutionStateRecord record)
    {
        EnsureDatabase(workspaceRoot);

        var dbPath = GetDatabasePath(workspaceRoot);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        INSERT INTO workspace_execution_state (
            workspace_root,
            last_failure_tool_name,
            last_failure_outcome_type,
            last_failure_target_path,
            last_failure_summary,
            last_failure_data_json,
            last_failure_utc,
            last_success_tool_name,
            last_success_outcome_type,
            last_success_target_path,
            last_success_summary,
            last_success_data_json,
            last_success_utc,
            last_verification_plan_id,
            last_verified_patch_draft_id,
            last_verification_tool_name,
            last_verification_outcome_type,
            last_verification_target_path,
            last_verification_summary,
            last_verification_data_json,
            last_verification_utc,
            last_detected_build_system_type,
            last_selected_build_profile_type,
            last_selected_build_profile_target_path,
            last_selected_build_profile_json,
            last_configure_tool_name,
            last_build_tool_family,
            last_verification_family
        )
        VALUES (
            $workspaceRoot,
            $lastFailureToolName,
            $lastFailureOutcomeType,
            $lastFailureTargetPath,
            $lastFailureSummary,
            $lastFailureDataJson,
            $lastFailureUtc,
            $lastSuccessToolName,
            $lastSuccessOutcomeType,
            $lastSuccessTargetPath,
            $lastSuccessSummary,
            $lastSuccessDataJson,
            $lastSuccessUtc,
            $lastVerificationPlanId,
            $lastVerifiedPatchDraftId,
            $lastVerificationToolName,
            $lastVerificationOutcomeType,
            $lastVerificationTargetPath,
            $lastVerificationSummary,
            $lastVerificationDataJson,
            $lastVerificationUtc,
            $lastDetectedBuildSystemType,
            $lastSelectedBuildProfileType,
            $lastSelectedBuildProfileTargetPath,
            $lastSelectedBuildProfileJson,
            $lastConfigureToolName,
            $lastBuildToolFamily,
            $lastVerificationFamily
        )
        ON CONFLICT(workspace_root) DO UPDATE SET
            last_failure_tool_name = excluded.last_failure_tool_name,
            last_failure_outcome_type = excluded.last_failure_outcome_type,
            last_failure_target_path = excluded.last_failure_target_path,
            last_failure_summary = excluded.last_failure_summary,
            last_failure_data_json = excluded.last_failure_data_json,
            last_failure_utc = excluded.last_failure_utc,
            last_success_tool_name = excluded.last_success_tool_name,
            last_success_outcome_type = excluded.last_success_outcome_type,
            last_success_target_path = excluded.last_success_target_path,
            last_success_summary = excluded.last_success_summary,
            last_success_data_json = excluded.last_success_data_json,
            last_success_utc = excluded.last_success_utc,
            last_verification_plan_id = excluded.last_verification_plan_id,
            last_verified_patch_draft_id = excluded.last_verified_patch_draft_id,
            last_verification_tool_name = excluded.last_verification_tool_name,
            last_verification_outcome_type = excluded.last_verification_outcome_type,
               last_verification_target_path = excluded.last_verification_target_path,
               last_verification_summary = excluded.last_verification_summary,
               last_verification_data_json = excluded.last_verification_data_json,
               last_verification_utc = excluded.last_verification_utc,
               last_detected_build_system_type = excluded.last_detected_build_system_type,
               last_selected_build_profile_type = excluded.last_selected_build_profile_type,
               last_selected_build_profile_target_path = excluded.last_selected_build_profile_target_path,
               last_selected_build_profile_json = excluded.last_selected_build_profile_json,
               last_configure_tool_name = excluded.last_configure_tool_name,
               last_build_tool_family = excluded.last_build_tool_family,
               last_verification_family = excluded.last_verification_family;
        """;

        command.Parameters.AddWithValue("$workspaceRoot", record.WorkspaceRoot ?? workspaceRoot);
        command.Parameters.AddWithValue("$lastFailureToolName", record.LastFailureToolName ?? "");
        command.Parameters.AddWithValue("$lastFailureOutcomeType", record.LastFailureOutcomeType ?? "");
        command.Parameters.AddWithValue("$lastFailureTargetPath", record.LastFailureTargetPath ?? "");
        command.Parameters.AddWithValue("$lastFailureSummary", record.LastFailureSummary ?? "");
        command.Parameters.AddWithValue("$lastFailureDataJson", record.LastFailureDataJson ?? "");
        command.Parameters.AddWithValue("$lastFailureUtc", record.LastFailureUtc ?? "");
        command.Parameters.AddWithValue("$lastSuccessToolName", record.LastSuccessToolName ?? "");
        command.Parameters.AddWithValue("$lastSuccessOutcomeType", record.LastSuccessOutcomeType ?? "");
        command.Parameters.AddWithValue("$lastSuccessTargetPath", record.LastSuccessTargetPath ?? "");
        command.Parameters.AddWithValue("$lastSuccessSummary", record.LastSuccessSummary ?? "");
        command.Parameters.AddWithValue("$lastSuccessDataJson", record.LastSuccessDataJson ?? "");
        command.Parameters.AddWithValue("$lastSuccessUtc", record.LastSuccessUtc ?? "");
        command.Parameters.AddWithValue("$lastVerificationPlanId", record.LastVerificationPlanId ?? "");
        command.Parameters.AddWithValue("$lastVerifiedPatchDraftId", record.LastVerifiedPatchDraftId ?? "");
        command.Parameters.AddWithValue("$lastVerificationToolName", record.LastVerificationToolName ?? "");
        command.Parameters.AddWithValue("$lastVerificationOutcomeType", record.LastVerificationOutcomeType ?? "");
        command.Parameters.AddWithValue("$lastVerificationTargetPath", record.LastVerificationTargetPath ?? "");
        command.Parameters.AddWithValue("$lastVerificationSummary", record.LastVerificationSummary ?? "");
        command.Parameters.AddWithValue("$lastVerificationDataJson", record.LastVerificationDataJson ?? "");
        command.Parameters.AddWithValue("$lastVerificationUtc", record.LastVerificationUtc ?? "");
        command.Parameters.AddWithValue("$lastDetectedBuildSystemType", record.LastDetectedBuildSystemType ?? "");
        command.Parameters.AddWithValue("$lastSelectedBuildProfileType", record.LastSelectedBuildProfileType ?? "");
        command.Parameters.AddWithValue("$lastSelectedBuildProfileTargetPath", record.LastSelectedBuildProfileTargetPath ?? "");
        command.Parameters.AddWithValue("$lastSelectedBuildProfileJson", record.LastSelectedBuildProfileJson ?? "");
        command.Parameters.AddWithValue("$lastConfigureToolName", record.LastConfigureToolName ?? "");
        command.Parameters.AddWithValue("$lastBuildToolFamily", record.LastBuildToolFamily ?? "");
        command.Parameters.AddWithValue("$lastVerificationFamily", record.LastVerificationFamily ?? "");
        command.ExecuteNonQuery();
    }

    private static WorkspaceExecutionStateRecord ReadExecutionState(SqliteDataReader reader)
    {
        return new WorkspaceExecutionStateRecord
        {
            WorkspaceRoot = reader.IsDBNull(0) ? "" : reader.GetString(0),
            LastFailureToolName = reader.IsDBNull(1) ? "" : reader.GetString(1),
            LastFailureOutcomeType = reader.IsDBNull(2) ? "" : reader.GetString(2),
            LastFailureTargetPath = reader.IsDBNull(3) ? "" : reader.GetString(3),
            LastFailureSummary = reader.IsDBNull(4) ? "" : reader.GetString(4),
            LastFailureDataJson = reader.IsDBNull(5) ? "" : reader.GetString(5),
            LastFailureUtc = reader.IsDBNull(6) ? "" : reader.GetString(6),
            LastSuccessToolName = reader.IsDBNull(7) ? "" : reader.GetString(7),
            LastSuccessOutcomeType = reader.IsDBNull(8) ? "" : reader.GetString(8),
            LastSuccessTargetPath = reader.IsDBNull(9) ? "" : reader.GetString(9),
            LastSuccessSummary = reader.IsDBNull(10) ? "" : reader.GetString(10),
            LastSuccessDataJson = reader.IsDBNull(11) ? "" : reader.GetString(11),
            LastSuccessUtc = reader.IsDBNull(12) ? "" : reader.GetString(12),
            LastVerificationPlanId = reader.IsDBNull(13) ? "" : reader.GetString(13),
            LastVerifiedPatchDraftId = reader.IsDBNull(14) ? "" : reader.GetString(14),
            LastVerificationToolName = reader.IsDBNull(15) ? "" : reader.GetString(15),
            LastVerificationOutcomeType = reader.IsDBNull(16) ? "" : reader.GetString(16),
            LastVerificationTargetPath = reader.IsDBNull(17) ? "" : reader.GetString(17),
            LastVerificationSummary = reader.IsDBNull(18) ? "" : reader.GetString(18),
            LastVerificationDataJson = reader.IsDBNull(19) ? "" : reader.GetString(19),
            LastVerificationUtc = reader.IsDBNull(20) ? "" : reader.GetString(20),
            LastDetectedBuildSystemType = reader.IsDBNull(21) ? "" : reader.GetString(21),
            LastSelectedBuildProfileType = reader.IsDBNull(22) ? "" : reader.GetString(22),
            LastSelectedBuildProfileTargetPath = reader.IsDBNull(23) ? "" : reader.GetString(23),
            LastSelectedBuildProfileJson = reader.IsDBNull(24) ? "" : reader.GetString(24),
            LastConfigureToolName = reader.IsDBNull(25) ? "" : reader.GetString(25),
            LastBuildToolFamily = reader.IsDBNull(26) ? "" : reader.GetString(26),
            LastVerificationFamily = reader.IsDBNull(27) ? "" : reader.GetString(27)
        };
    }

    private static void EnsureTableSchema(
        SqliteConnection connection,
        string workspaceRoot,
        TableSchemaDefinition schema,
        List<string> migrationMessages)
    {
        ExecuteMigrationSql(connection, schema.TableName, "", schema.CreateSql);

        var existingColumns = LoadExistingColumns(connection, schema.TableName);
        foreach (var column in schema.Columns)
        {
            if (existingColumns.Contains(column.Name))
                continue;

            if (string.IsNullOrWhiteSpace(column.AddColumnDefinition))
            {
                throw new InvalidOperationException(
                    $"DB migration failed for workspace '{workspaceRoot}': table '{schema.TableName}' is missing required column '{column.Name}', and RAM does not have a safe in-place add-column definition for it.");
            }

            var sql = $"ALTER TABLE {schema.TableName} ADD COLUMN {column.Name} {column.AddColumnDefinition};";
            ExecuteMigrationSql(connection, schema.TableName, column.Name, sql);
            migrationMessages.Add($"DB migration: added {schema.TableName}.{column.Name}");
            existingColumns.Add(column.Name);
        }

        foreach (var indexSql in schema.IndexStatements)
            ExecuteMigrationSql(connection, schema.TableName, "", indexSql);
    }

    private static HashSet<string> LoadExistingColumns(SqliteConnection connection, string tableName)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            existingColumns.Add(reader.IsDBNull(1) ? "" : reader.GetString(1));
        }

        return existingColumns;
    }

    private static void ExecuteMigrationSql(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            var columnSuffix = string.IsNullOrWhiteSpace(columnName)
                ? ""
                : $", column '{columnName}'";
            throw new InvalidOperationException(
                $"DB migration failed for table '{tableName}'{columnSuffix}. SQL step: {sql.Trim()} Error: {ex.Message}",
                ex);
        }
    }

    private static void EnsureSchemaVersionMetadata(
        SqliteConnection connection,
        string workspaceRoot,
        List<string> migrationMessages)
    {
        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText =
        """
        SELECT value
        FROM ram_metadata
        WHERE key = 'schema_version';
        """;

        var currentValue = selectCommand.ExecuteScalar() as string ?? "";
        if (string.Equals(currentValue, CurrentSchemaVersion, StringComparison.Ordinal))
            return;

        using var upsertCommand = connection.CreateCommand();
        upsertCommand.CommandText =
        """
        INSERT INTO ram_metadata (key, value)
        VALUES ('schema_version', $value)
        ON CONFLICT(key) DO UPDATE SET
            value = excluded.value;
        """;
        upsertCommand.Parameters.AddWithValue("$value", CurrentSchemaVersion);
        upsertCommand.ExecuteNonQuery();

        migrationMessages.Add(string.IsNullOrWhiteSpace(currentValue)
            ? $"DB migration: set schema version {CurrentSchemaVersion}"
            : $"DB migration: schema version {currentValue} -> {CurrentSchemaVersion}");
    }

    private void StoreMigrationMessages(string workspaceRoot, IReadOnlyList<string> migrationMessages)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || migrationMessages.Count == 0)
            return;

        lock (_migrationMessagesLock)
        {
            if (!_pendingMigrationMessages.TryGetValue(workspaceRoot, out var existing))
            {
                existing = [];
                _pendingMigrationMessages[workspaceRoot] = existing;
            }

            foreach (var message in migrationMessages)
            {
                if (!existing.Contains(message, StringComparer.Ordinal))
                    existing.Add(message);
            }
        }
    }

    private sealed class TableSchemaDefinition
    {
        public TableSchemaDefinition(
            string tableName,
            string createSql,
            IReadOnlyList<ColumnSchemaDefinition> columns,
            IReadOnlyList<string>? indexStatements = null)
        {
            TableName = tableName;
            CreateSql = createSql;
            Columns = columns;
            IndexStatements = indexStatements ?? [];
        }

        public string TableName { get; }
        public string CreateSql { get; }
        public IReadOnlyList<ColumnSchemaDefinition> Columns { get; }
        public IReadOnlyList<string> IndexStatements { get; }
    }

    private sealed class ColumnSchemaDefinition
    {
        public ColumnSchemaDefinition(string name, string createDefinition, string? addColumnDefinition)
        {
            Name = name;
            CreateDefinition = createDefinition;
            AddColumnDefinition = addColumnDefinition;
        }

        public string Name { get; }
        public string CreateDefinition { get; }
        public string? AddColumnDefinition { get; }
    }
}
