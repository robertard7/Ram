using System.IO;
using Microsoft.Data.Sqlite;
using RAM.Models;

namespace RAM.Services;

public sealed class RamDbService
{
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
        var dbPath = GetDatabasePath(workspaceRoot);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
        """
        CREATE TABLE IF NOT EXISTS intents (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            title TEXT NOT NULL,
            objective TEXT NOT NULL,
            notes TEXT NOT NULL,
            last_updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS memory_summaries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            workspace_root TEXT NOT NULL,
            source_type TEXT NOT NULL,
            source_id TEXT NOT NULL,
            summary_text TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_memory_summaries_workspace
        ON memory_summaries (workspace_root);

        CREATE INDEX IF NOT EXISTS idx_memory_summaries_source
        ON memory_summaries (source_type, source_id);
        """;
        command.ExecuteNonQuery();
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
}