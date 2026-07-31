using System.IO;
using System.Globalization;
using Microsoft.Data.Sqlite;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public sealed class InspectionResultStore
{
    public const int DefaultMaxRecords = 1_000_000;
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    private const int ScoreRoundDigits = 6;

    private readonly int _maxRecords;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public InspectionResultStore(string databasePath, int maxRecords = DefaultMaxRecords)
    {
        DatabasePath = databasePath;
        _maxRecords = Math.Max(1, maxRecords);
    }

    public string DatabasePath { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                create table if not exists inspection_results (
                    id integer primary key autoincrement,
                    occurred_at text not null,
                    cycle_id integer not null,
                    product_code text not null,
                    serial_number text,
                    camera_id text,
                    camera_name text not null,
                    task_id text not null,
                    task_name text not null,
                    vector_set_id text,
                    raw_image_path text,
                    crop_image_path text,
                    result text not null,
                    ok_score real,
                    ng_score real,
                    margin real,
                    threshold real,
                    top_k integer,
                    elapsed_ms real,
                    error_message text,
                    learning_state text not null,
                    top_ok_similarity real,
                    top_ok_image_path text,
                    top_ng_similarity real,
                    top_ng_image_path text
                );

                create index if not exists ix_inspection_results_vector_state
                    on inspection_results(vector_set_id, learning_state, id desc);

                create index if not exists ix_inspection_results_vector_result
                    on inspection_results(vector_set_id, result, id desc);

                create index if not exists ix_inspection_results_occurred_id
                    on inspection_results(occurred_at, id desc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureColumnAsync(connection, "inspection_results", "serial_number", "text", cancellationToken);
            await DropColumnIfExistsAsync(connection, "inspection_results", "backbone_type", cancellationToken);
            await NormalizeExistingNumericValuesAsync(connection, cancellationToken);
            await NormalizeExistingTopMatchImagePathsAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async ValueTask<long> AddAsync(
        InspectionResultRecord record,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            insert into inspection_results(
                occurred_at, cycle_id, product_code, serial_number, camera_id, camera_name,
                task_id, task_name, vector_set_id, raw_image_path, crop_image_path,
                result, ok_score, ng_score, margin, threshold, top_k, elapsed_ms, error_message,
                learning_state, top_ok_similarity, top_ok_image_path, top_ng_similarity, top_ng_image_path)
            values(
                $occurred_at, $cycle_id, $product_code, $serial_number, $camera_id, $camera_name,
                $task_id, $task_name, $vector_set_id, $raw_image_path, $crop_image_path,
                $result, $ok_score, $ng_score, $margin, $threshold, $top_k, $elapsed_ms, $error_message,
                $learning_state, $top_ok_similarity, $top_ok_image_path, $top_ng_similarity, $top_ng_image_path);
            select last_insert_rowid();
            """;
        AddRecordParameters(command, record);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        await PruneAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async ValueTask<IReadOnlyList<InspectionResultRecord>> ListOkCandidatesAsync(
        string vectorSetId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return await ListAsync(
            """
            select * from inspection_results
            where vector_set_id = $vector_set_id
              and learning_state = 'OkCandidate'
              and crop_image_path is not null
            order by id desc
            limit $limit;
            """,
            vectorSetId,
            limit,
            cancellationToken);
    }

    public async ValueTask<IReadOnlyList<InspectionResultRecord>> ListRecentNgAsync(
        string vectorSetId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return await ListAsync(
            """
            select * from inspection_results
            where vector_set_id = $vector_set_id
              and result = 'NG'
              and crop_image_path is not null
            order by id desc
            limit $limit;
            """,
            vectorSetId,
            limit,
            cancellationToken);
    }

    public async ValueTask<IReadOnlyList<InspectionResultRecord>> ListRecentRecordsAsync(
        string vectorSetId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return await ListAsync(
            """
            select * from inspection_results
            where vector_set_id = $vector_set_id
              and crop_image_path is not null
              and learning_state not in ('AddedOk', 'AddedNg')
            order by id desc
            limit $limit;
            """,
            vectorSetId,
            limit,
            cancellationToken);
    }

    public async ValueTask<IReadOnlyList<InspectionResultRecord>> QueryAsync(
        InspectionResultQuery query,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var conditions = new List<string>();
        if (query.StartTime.HasValue)
        {
            conditions.Add("occurred_at >= $start_time");
            command.Parameters.AddWithValue(
                "$start_time",
                query.StartTime.Value.ToLocalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
        }

        if (query.EndTime.HasValue)
        {
            conditions.Add("occurred_at <= $end_time");
            command.Parameters.AddWithValue(
                "$end_time",
                query.EndTime.Value.ToLocalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
        }

        AddLikeCondition(command, conditions, "product_code", "$product_code", query.ProductCode);
        AddLikeCondition(command, conditions, "serial_number", "$serial_number", query.SerialNumber);
        AddLikeCondition(command, conditions, "camera_name", "$camera_name", query.CameraName);
        AddTaskCondition(command, conditions, query.TaskName);

        if (!string.IsNullOrWhiteSpace(query.Result))
        {
            conditions.Add("result = $result");
            command.Parameters.AddWithValue("$result", query.Result.Trim());
        }

        var where = conditions.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}where {string.Join(" and ", conditions)}";
        var limit = query.Limit.HasValue
            ? $"{Environment.NewLine}limit $limit"
            : string.Empty;
        command.CommandText = $"""
            select * from inspection_results
            {where}
            order by id desc
            {limit};
            """;

        if (query.Limit.HasValue)
        {
            command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit.Value, 1, _maxRecords));
        }

        var records = new List<InspectionResultRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public async ValueTask<IReadOnlyList<string>> ListTaskNamesAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select task_name, max(id) as latest_id
            from inspection_results
            where task_name is not null and trim(task_name) <> ''
            group by task_name
            order by latest_id desc
            limit $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public async ValueTask<bool> UpdateLearningStateAsync(
        long id,
        string learningState,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update inspection_results
            set learning_state = $learning_state
            where id = $id;
            """;
        command.Parameters.AddWithValue("$learning_state", learningState);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async ValueTask<IReadOnlyList<InspectionResultRecord>> ListAsync(
        string commandText,
        string vectorSetId,
        int limit,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$vector_set_id", vectorSetId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var records = new List<InspectionResultRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private async ValueTask PruneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            delete from inspection_results
            where id <= (
                select coalesce(max(id), 0) - $max_records
                from inspection_results
            );
            """;
        command.Parameters.AddWithValue("$max_records", _maxRecords);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection()
    {
        return new SqliteConnection($"Data Source={DatabasePath}");
    }

    private static async ValueTask EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"pragma table_info({tableName});";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"alter table {tableName} add column {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask DropColumnIfExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(connection, tableName, columnName, cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"alter table {tableName} drop column {columnName};";
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException) when (
            string.Equals(tableName, "inspection_results", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(columnName, "backbone_type", StringComparison.OrdinalIgnoreCase))
        {
            await RebuildInspectionResultsWithoutBackboneAsync(connection, cancellationToken);
        }
    }

    private static async ValueTask RebuildInspectionResultsWithoutBackboneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            drop table if exists inspection_results_new;

            create table inspection_results_new (
                id integer primary key autoincrement,
                occurred_at text not null,
                cycle_id integer not null,
                product_code text not null,
                serial_number text,
                camera_id text,
                camera_name text not null,
                task_id text not null,
                task_name text not null,
                vector_set_id text,
                raw_image_path text,
                crop_image_path text,
                result text not null,
                ok_score real,
                ng_score real,
                margin real,
                threshold real,
                top_k integer,
                elapsed_ms real,
                error_message text,
                learning_state text not null,
                top_ok_similarity real,
                top_ok_image_path text,
                top_ng_similarity real,
                top_ng_image_path text
            );

            insert into inspection_results_new(
                id, occurred_at, cycle_id, product_code, serial_number, camera_id, camera_name,
                task_id, task_name, vector_set_id, raw_image_path, crop_image_path,
                result, ok_score, ng_score, margin, threshold, top_k, elapsed_ms, error_message,
                learning_state, top_ok_similarity, top_ok_image_path, top_ng_similarity, top_ng_image_path)
            select
                id, occurred_at, cycle_id, product_code, serial_number, camera_id, camera_name,
                task_id, task_name, vector_set_id, raw_image_path, crop_image_path,
                result, ok_score, ng_score, margin, threshold, top_k, elapsed_ms, error_message,
                learning_state, top_ok_similarity, top_ok_image_path, top_ng_similarity, top_ng_image_path
            from inspection_results;

            drop table inspection_results;
            alter table inspection_results_new rename to inspection_results;

            create index if not exists ix_inspection_results_vector_state
                on inspection_results(vector_set_id, learning_state, id desc);

            create index if not exists ix_inspection_results_vector_result
                on inspection_results(vector_set_id, result, id desc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"pragma table_info({tableName});";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask NormalizeExistingNumericValuesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update inspection_results
            set ok_score = case when ok_score is null then null else round(ok_score, 6) end,
                ng_score = case when ng_score is null then null else round(ng_score, 6) end,
                margin = case when margin is null then null else round(margin, 6) end,
                threshold = case when threshold is null then null else round(threshold, 6) end,
                top_ok_similarity = case when top_ok_similarity is null then null else round(top_ok_similarity, 6) end,
                top_ng_similarity = case when top_ng_similarity is null then null else round(top_ng_similarity, 6) end;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask NormalizeExistingTopMatchImagePathsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = """
            select id, top_ok_image_path, top_ng_image_path
            from inspection_results
            where top_ok_image_path like '%\%'
               or top_ok_image_path like '%/%'
               or top_ng_image_path like '%\%'
               or top_ng_image_path like '%/%';
            """;

        var updates = new List<(long Id, string? TopOk, string? TopNg)>();
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                updates.Add((
                    reader.GetInt64(0),
                    NormalizeImageFileName(reader.IsDBNull(1) ? null : reader.GetString(1)),
                    NormalizeImageFileName(reader.IsDBNull(2) ? null : reader.GetString(2))));
            }
        }

        if (updates.Count == 0)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var update in updates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                update inspection_results
                set top_ok_image_path = $top_ok_image_path,
                    top_ng_image_path = $top_ng_image_path
                where id = $id;
                """;
            command.Parameters.AddWithValue("$id", update.Id);
            command.Parameters.AddWithValue("$top_ok_image_path", Db(update.TopOk));
            command.Parameters.AddWithValue("$top_ng_image_path", Db(update.TopNg));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void AddRecordParameters(SqliteCommand command, InspectionResultRecord record)
    {
        command.Parameters.AddWithValue("$occurred_at", record.OccurredAt.ToLocalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cycle_id", record.CycleId);
        command.Parameters.AddWithValue("$product_code", record.ProductCode);
        command.Parameters.AddWithValue("$serial_number", Db(record.SerialNumber));
        command.Parameters.AddWithValue("$camera_id", Db(record.CameraId));
        command.Parameters.AddWithValue("$camera_name", record.CameraName);
        command.Parameters.AddWithValue("$task_id", record.TaskId);
        command.Parameters.AddWithValue("$task_name", record.TaskName);
        command.Parameters.AddWithValue("$vector_set_id", Db(record.VectorSetId));
        command.Parameters.AddWithValue("$raw_image_path", Db(record.RawImagePath));
        command.Parameters.AddWithValue("$crop_image_path", Db(record.CropImagePath));
        command.Parameters.AddWithValue("$result", record.Result);
        AddReal(command, "$ok_score", record.OkScore);
        AddReal(command, "$ng_score", record.NgScore);
        AddReal(command, "$margin", record.Margin);
        AddReal(command, "$threshold", record.Threshold);
        command.Parameters.AddWithValue("$top_k", Db(record.TopK));
        AddReal(command, "$elapsed_ms", record.ElapsedMs);
        command.Parameters.AddWithValue("$error_message", Db(record.ErrorMessage));
        command.Parameters.AddWithValue("$learning_state", record.LearningState);
        AddReal(command, "$top_ok_similarity", record.TopOkSimilarity);
        command.Parameters.AddWithValue("$top_ok_image_path", Db(NormalizeImageFileName(record.TopOkImagePath)));
        AddReal(command, "$top_ng_similarity", record.TopNgSimilarity);
        command.Parameters.AddWithValue("$top_ng_image_path", Db(NormalizeImageFileName(record.TopNgImagePath)));
    }

    private static InspectionResultRecord ReadRecord(SqliteDataReader reader)
    {
        return new InspectionResultRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            OccurredAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("occurred_at"))),
            CycleId = reader.GetInt64(reader.GetOrdinal("cycle_id")),
            ProductCode = reader.GetString(reader.GetOrdinal("product_code")),
            SerialNumber = GetString(reader, "serial_number"),
            CameraId = GetString(reader, "camera_id"),
            CameraName = reader.GetString(reader.GetOrdinal("camera_name")),
            TaskId = reader.GetString(reader.GetOrdinal("task_id")),
            TaskName = reader.GetString(reader.GetOrdinal("task_name")),
            VectorSetId = GetString(reader, "vector_set_id"),
            RawImagePath = GetString(reader, "raw_image_path"),
            CropImagePath = GetString(reader, "crop_image_path"),
            Result = reader.GetString(reader.GetOrdinal("result")),
            OkScore = GetFloat(reader, "ok_score"),
            NgScore = GetFloat(reader, "ng_score"),
            Margin = GetFloat(reader, "margin"),
            Threshold = GetFloat(reader, "threshold"),
            TopK = GetInt(reader, "top_k"),
            ElapsedMs = GetDouble(reader, "elapsed_ms"),
            ErrorMessage = GetString(reader, "error_message"),
            LearningState = reader.GetString(reader.GetOrdinal("learning_state")),
            TopOkSimilarity = GetFloat(reader, "top_ok_similarity"),
            TopOkImagePath = GetString(reader, "top_ok_image_path"),
            TopNgSimilarity = GetFloat(reader, "top_ng_similarity"),
            TopNgImagePath = GetString(reader, "top_ng_image_path")
        };
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object Db(int? value) => value.HasValue ? value.Value : DBNull.Value;

    private static void AddReal(SqliteCommand command, string name, float? value)
    {
        var parameter = command.Parameters.Add(name, SqliteType.Real);
        parameter.Value = value.HasValue
            ? Math.Round(value.Value, ScoreRoundDigits, MidpointRounding.AwayFromZero)
            : DBNull.Value;
    }

    private static void AddReal(SqliteCommand command, string name, double? value)
    {
        var parameter = command.Parameters.Add(name, SqliteType.Real);
        parameter.Value = value.HasValue
            ? Math.Round(value.Value, ScoreRoundDigits, MidpointRounding.AwayFromZero)
            : DBNull.Value;
    }

    private static string? NormalizeImageFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Trim();
        return normalized.Contains(Path.DirectorySeparatorChar) ||
               normalized.Contains(Path.AltDirectorySeparatorChar) ||
               normalized.Contains('\\') ||
               normalized.Contains('/')
            ? Path.GetFileName(normalized)
            : normalized;
    }

    private static void AddLikeCondition(
        SqliteCommand command,
        ICollection<string> conditions,
        string columnName,
        string parameterName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        conditions.Add($"{columnName} like {parameterName} escape '\\'");
        command.Parameters.AddWithValue(parameterName, $"%{EscapeLike(value.Trim())}%");
    }

    private static void AddTaskCondition(
        SqliteCommand command,
        ICollection<string> conditions,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        conditions.Add("(task_id like $task escape '\\' or task_name like $task escape '\\')");
        command.Parameters.AddWithValue("$task", $"%{EscapeLike(value.Trim())}%");
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static string? GetString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static float? GetFloat(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : (float)reader.GetDouble(ordinal);
    }

    private static double? GetDouble(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static int? GetInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }
}

public sealed class InspectionResultRecord
{
    public long Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;

    public long CycleId { get; init; }

    public required string ProductCode { get; init; }

    public string? SerialNumber { get; init; }

    public string? CameraId { get; init; }

    public required string CameraName { get; init; }

    public required string TaskId { get; init; }

    public required string TaskName { get; init; }

    public string? VectorSetId { get; init; }

    public string? RawImagePath { get; init; }

    public string? CropImagePath { get; init; }

    public required string Result { get; init; }

    public float? OkScore { get; init; }

    public float? NgScore { get; init; }

    public float? Margin { get; init; }

    public float? Threshold { get; init; }

    public int? TopK { get; init; }

    public double? ElapsedMs { get; init; }

    public string? ErrorMessage { get; init; }

    public string LearningState { get; init; } = InspectionLearningStates.None;

    public float? TopOkSimilarity { get; init; }

    public string? TopOkImagePath { get; init; }

    public float? TopNgSimilarity { get; init; }

    public string? TopNgImagePath { get; init; }
}

public sealed class InspectionResultQuery
{
    public DateTimeOffset? StartTime { get; init; }

    public DateTimeOffset? EndTime { get; init; }

    public string? ProductCode { get; init; }

    public string? SerialNumber { get; init; }

    public string? CameraName { get; init; }

    public string? TaskName { get; init; }

    public string? Result { get; init; }

    public int? Limit { get; init; } = 100;
}

public static class InspectionLearningStates
{
    public const string None = "None";
    public const string OkCandidate = "OkCandidate";
    public const string AddedOk = "AddedOk";
    public const string AddedNg = "AddedNg";
    public const string Ignored = "Ignored";
}
