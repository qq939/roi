using ClipInspect.Matching;
using Microsoft.Data.Sqlite;

namespace ClipInspect.Storage.Sqlite;

public sealed class SqliteVectorStore
{
    public string DatabasePath { get; }

    public SqliteVectorStore(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, """
            create table if not exists products (
                product_id text primary key,
                name text,
                model_name text not null,
                pretrained text not null,
                feature_dim integer not null,
                top_k integer not null,
                threshold real not null,
                text_weight real not null,
                created_at text not null,
                updated_at text not null
            );

            create table if not exists samples (
                id integer primary key autoincrement,
                product_id text not null,
                label text not null,
                kind text not null,
                image_path text,
                prompt text,
                feature blob not null,
                enabled integer not null default 1,
                source text not null default 'Manual',
                note text,
                created_at text not null,
                updated_at text not null,
                foreign key(product_id) references products(product_id) on delete cascade
            );

            create index if not exists ix_samples_product_label_kind
                on samples(product_id, label, kind, enabled);
            create index if not exists ix_samples_image_path
                on samples(product_id, kind, image_path);
            create index if not exists ix_samples_prompt
                on samples(product_id, kind, prompt);
            """, cancellationToken);

        await EnsureColumnAsync(connection, "products", "name", "text", cancellationToken);
        await EnsureColumnAsync(connection, "products", "created_at", "text not null default ''", cancellationToken);
        await EnsureColumnAsync(connection, "samples", "enabled", "integer not null default 1", cancellationToken);
        await EnsureColumnAsync(connection, "samples", "source", "text not null default 'Manual'", cancellationToken);
        await EnsureColumnAsync(connection, "samples", "note", "text", cancellationToken);
        await EnsureColumnAsync(connection, "samples", "updated_at", "text not null default ''", cancellationToken);
    }

    public async ValueTask CreateOrUpdateProductAsync(
        SqliteProductConfig config,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await UpsertProductAsync(connection, config, cancellationToken);
    }

    public async ValueTask ImportCacheAsync(ClipCache cache, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertProductAsync(connection, SqliteProductConfig.FromCache(cache), cancellationToken);
        await DeleteProductSamplesAsync(connection, cache.ProductId, cancellationToken);

        foreach (var item in cache.OkItems)
        {
            await UpsertImageSampleAsync(connection, cache.ProductId, "OK", item.ImagePath, item.Feature, "Import", null, cancellationToken);
        }

        foreach (var item in cache.NgItems)
        {
            await UpsertImageSampleAsync(connection, cache.ProductId, "NG", item.ImagePath, item.Feature, "Import", null, cancellationToken);
        }

        foreach (var item in cache.OkTextItems)
        {
            await UpsertTextSampleAsync(connection, cache.ProductId, "OK", item.Prompt, item.Feature, "Import", null, cancellationToken);
        }

        foreach (var item in cache.NgTextItems)
        {
            await UpsertTextSampleAsync(connection, cache.ProductId, "NG", item.Prompt, item.Feature, "Import", null, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<ClipCache> LoadCacheAsync(string productId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        var product = await LoadProductAsync(connection, productId, cancellationToken)
            ?? throw new InvalidOperationException($"Product not found in SQLite vector store: {productId}");
        var samples = await ListSamplesAsync(connection, productId, null, null, enabledOnly: true, cancellationToken);

        return new ClipCache
        {
            ProductId = product.ProductId,
            ModelName = product.ModelName,
            Pretrained = product.Pretrained,
            FeatureDim = product.FeatureDim,
            TopK = product.TopK,
            Threshold = product.Threshold,
            TextWeight = product.TextWeight,
            OkItems = samples
                .Where(item => item.Label == "OK" && item.Kind == "Image")
                .Select(item => new ImageCacheItem { ImagePath = item.ImagePath ?? "", Feature = item.Feature })
                .ToArray(),
            NgItems = samples
                .Where(item => item.Label == "NG" && item.Kind == "Image")
                .Select(item => new ImageCacheItem { ImagePath = item.ImagePath ?? "", Feature = item.Feature })
                .ToArray(),
            OkTextItems = samples
                .Where(item => item.Label == "OK" && item.Kind == "Text")
                .Select(item => new TextCacheItem { Prompt = item.Prompt ?? "", Feature = item.Feature })
                .ToArray(),
            NgTextItems = samples
                .Where(item => item.Label == "NG" && item.Kind == "Text")
                .Select(item => new TextCacheItem { Prompt = item.Prompt ?? "", Feature = item.Feature })
                .ToArray()
        };
    }

    public async ValueTask<IReadOnlyList<SqliteProductInfo>> ListProductsAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select product_id, name, model_name, pretrained, feature_dim, top_k, threshold, text_weight, created_at, updated_at
            from products
            order by product_id;
            """;

        var products = new List<SqliteProductInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(ReadProductInfo(reader));
        }

        return products;
    }

    public async ValueTask<SqliteProductInfo?> GetProductAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var product = await LoadProductAsync(connection, productId, cancellationToken);
        return product?.ToInfo();
    }

    public async ValueTask<IReadOnlyList<SqliteVectorSample>> ListSamplesAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        return await ListSamplesAsync(connection, productId, null, null, enabledOnly: false, cancellationToken);
    }

    public async ValueTask<long> AddImageSampleAsync(
        string productId,
        string label,
        string imagePath,
        float[] feature,
        string source = "Manual",
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        return await UpsertImageSampleAsync(connection, productId, label, imagePath, feature, source, note, cancellationToken);
    }

    public async ValueTask<long> AddTextSampleAsync(
        string productId,
        string label,
        string prompt,
        float[] feature,
        string source = "Manual",
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        return await UpsertTextSampleAsync(connection, productId, label, prompt, feature, source, note, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<SqliteVectorSearchResult>> SearchAsync(
        string productId,
        string label,
        string kind,
        float[] queryFeature,
        int topK,
        CancellationToken cancellationToken)
    {
        return await SearchAsync(productId, label, kind, queryFeature, topK, null, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<SqliteVectorSearchResult>> SearchAsync(
        string productId,
        string label,
        string kind,
        float[] queryFeature,
        int topK,
        float? minSimilarity = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var query = VectorMath.NormalizeCopy(queryFeature);
        var samples = await ListSamplesAsync(connection, productId, label, kind, enabledOnly: true, cancellationToken);

        return samples
            .Select(sample => new SqliteVectorSearchResult
            {
                Sample = sample,
                Similarity = VectorMath.Dot(query, sample.Feature)
            })
            .Where(item => minSimilarity is null || item.Similarity >= minSimilarity.Value)
            .OrderByDescending(item => item.Similarity)
            .Take(Math.Max(1, topK))
            .ToArray();
    }

    public async ValueTask<bool> DeleteSampleAsync(long sampleId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from samples where id = $id;";
        command.Parameters.AddWithValue("$id", sampleId);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        return count > 0;
    }

    public async ValueTask<bool> UpdateSampleLabelAsync(
        long sampleId,
        string label,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "update samples set label = $label, updated_at = $updated_at where id = $id;";
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", sampleId);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        return count > 0;
    }

    public async ValueTask<bool> DeleteImageSampleAsync(
        string productId,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            delete from samples
            where product_id = $product_id
              and kind = 'Image'
              and image_path = $image_path;
            """;
        command.Parameters.AddWithValue("$product_id", productId);
        command.Parameters.AddWithValue("$image_path", Path.GetFullPath(imagePath));
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        return count > 0;
    }

    public async ValueTask<bool> SetSampleEnabledAsync(
        long sampleId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "update samples set enabled = $enabled, updated_at = $updated_at where id = $id;";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", sampleId);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        return count > 0;
    }

    public async ValueTask<bool> DeleteProductAsync(string productId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await DeleteProductSamplesAsync(connection, productId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "delete from products where product_id = $product_id;";
        command.Parameters.AddWithValue("$product_id", productId);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count > 0;
    }

    private SqliteConnection OpenConnection()
    {
        return new SqliteConnection($"Data Source={DatabasePath}");
    }

    private static async ValueTask ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = $"pragma table_info({tableName});";
        await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await ExecuteNonQueryAsync(connection, $"alter table {tableName} add column {columnName} {definition};", cancellationToken);
    }

    private static async ValueTask UpsertProductAsync(
        SqliteConnection connection,
        SqliteProductConfig config,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into products(product_id, name, model_name, pretrained, feature_dim, top_k, threshold, text_weight, created_at, updated_at)
            values($product_id, $name, $model_name, $pretrained, $feature_dim, $top_k, $threshold, $text_weight, $created_at, $updated_at)
            on conflict(product_id) do update set
                name = excluded.name,
                model_name = excluded.model_name,
                pretrained = excluded.pretrained,
                feature_dim = excluded.feature_dim,
                top_k = excluded.top_k,
                threshold = excluded.threshold,
                text_weight = excluded.text_weight,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$product_id", config.ProductId);
        command.Parameters.AddWithValue("$name", (object?)config.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$model_name", config.ModelName);
        command.Parameters.AddWithValue("$pretrained", config.Pretrained);
        command.Parameters.AddWithValue("$feature_dim", config.FeatureDim);
        command.Parameters.AddWithValue("$top_k", config.TopK);
        command.Parameters.AddWithValue("$threshold", config.Threshold);
        command.Parameters.AddWithValue("$text_weight", config.TextWeight);
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask DeleteProductSamplesAsync(
        SqliteConnection connection,
        string productId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from samples where product_id = $product_id;";
        command.Parameters.AddWithValue("$product_id", productId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ValueTask<long> UpsertImageSampleAsync(
        SqliteConnection connection,
        string productId,
        string label,
        string imagePath,
        float[] feature,
        string source,
        string? note,
        CancellationToken cancellationToken)
    {
        return UpsertSampleAsync(connection, productId, label, "Image", Path.GetFullPath(imagePath), null, feature, source, note, cancellationToken);
    }

    private static ValueTask<long> UpsertTextSampleAsync(
        SqliteConnection connection,
        string productId,
        string label,
        string prompt,
        float[] feature,
        string source,
        string? note,
        CancellationToken cancellationToken)
    {
        return UpsertSampleAsync(connection, productId, label, "Text", null, prompt, feature, source, note, cancellationToken);
    }

    private static async ValueTask<long> UpsertSampleAsync(
        SqliteConnection connection,
        string productId,
        string label,
        string kind,
        string? imagePath,
        string? prompt,
        float[] feature,
        string source,
        string? note,
        CancellationToken cancellationToken)
    {
        var existingId = await FindExistingSampleIdAsync(connection, productId, kind, imagePath, prompt, cancellationToken);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        if (existingId is null)
        {
            command.CommandText = """
                insert into samples(product_id, label, kind, image_path, prompt, feature, enabled, source, note, created_at, updated_at)
                values($product_id, $label, $kind, $image_path, $prompt, $feature, 1, $source, $note, $created_at, $updated_at);
                select last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                update samples
                set label = $label,
                    feature = $feature,
                    enabled = 1,
                    source = $source,
                    note = $note,
                    updated_at = $updated_at
                where id = $id;
                select $id;
                """;
            command.Parameters.AddWithValue("$id", existingId.Value);
        }

        command.Parameters.AddWithValue("$product_id", productId);
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$image_path", (object?)imagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$prompt", (object?)prompt ?? DBNull.Value);
        command.Parameters.Add("$feature", SqliteType.Blob).Value = ToBlob(feature);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        var id = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(id);
    }

    private static async ValueTask<long?> FindExistingSampleIdAsync(
        SqliteConnection connection,
        string productId,
        string kind,
        string? imagePath,
        string? prompt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = kind == "Image"
            ? """
              select id from samples
              where product_id = $product_id
                and kind = 'Image'
                and image_path = $image_path
              limit 1;
              """
            : """
              select id from samples
              where product_id = $product_id
                and kind = 'Text'
                and prompt = $prompt
              limit 1;
              """;
        command.Parameters.AddWithValue("$product_id", productId);
        command.Parameters.AddWithValue("$image_path", (object?)imagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$prompt", (object?)prompt ?? DBNull.Value);
        var id = await command.ExecuteScalarAsync(cancellationToken);
        return id is null || id == DBNull.Value ? null : Convert.ToInt64(id);
    }

    private static async ValueTask<SqliteProduct?> LoadProductAsync(
        SqliteConnection connection,
        string productId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select product_id, name, model_name, pretrained, feature_dim, top_k, threshold, text_weight, created_at, updated_at
            from products
            where product_id = $product_id;
            """;
        command.Parameters.AddWithValue("$product_id", productId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadProduct(reader);
    }

    private static async ValueTask<IReadOnlyList<SqliteVectorSample>> ListSamplesAsync(
        SqliteConnection connection,
        string productId,
        string? label,
        string? kind,
        bool enabledOnly,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, product_id, label, kind, image_path, prompt, feature, enabled, source, note, created_at, updated_at
            from samples
            where product_id = $product_id
              and ($label is null or label = $label)
              and ($kind is null or kind = $kind)
              and ($enabled_only = 0 or enabled = 1);
            """;
        command.Parameters.AddWithValue("$product_id", productId);
        command.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled_only", enabledOnly ? 1 : 0);

        var samples = new List<SqliteVectorSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new SqliteVectorSample
            {
                Id = reader.GetInt64(0),
                ProductId = reader.GetString(1),
                Label = reader.GetString(2),
                Kind = reader.GetString(3),
                ImagePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                Prompt = reader.IsDBNull(5) ? null : reader.GetString(5),
                Feature = FromBlob((byte[])reader["feature"]),
                Enabled = reader.GetInt32(7) != 0,
                Source = reader.GetString(8),
                Note = reader.IsDBNull(9) ? null : reader.GetString(9),
                CreatedAt = reader.GetString(10),
                UpdatedAt = reader.GetString(11)
            });
        }

        return samples;
    }

    private static SqliteProduct ReadProduct(SqliteDataReader reader)
    {
        return new SqliteProduct(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetFloat(6),
            reader.GetFloat(7),
            reader.GetString(8),
            reader.GetString(9));
    }

    private static SqliteProductInfo ReadProductInfo(SqliteDataReader reader)
    {
        return ReadProduct(reader).ToInfo();
    }

    private static byte[] ToBlob(float[] feature)
    {
        var bytes = new byte[feature.Length * sizeof(float)];
        Buffer.BlockCopy(feature, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBlob(byte[] bytes)
    {
        var feature = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, feature, 0, bytes.Length);
        return feature;
    }

    private sealed record SqliteProduct(
        string ProductId,
        string? Name,
        string ModelName,
        string Pretrained,
        int FeatureDim,
        int TopK,
        float Threshold,
        float TextWeight,
        string CreatedAt,
        string UpdatedAt)
    {
        public SqliteProductInfo ToInfo()
        {
            return new SqliteProductInfo
            {
                ProductId = ProductId,
                Name = Name,
                ModelName = ModelName,
                Pretrained = Pretrained,
                FeatureDim = FeatureDim,
                TopK = TopK,
                Threshold = Threshold,
                TextWeight = TextWeight,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt
            };
        }
    }
}

public sealed class SqliteProductConfig
{
    public required string ProductId { get; init; }
    public string? Name { get; init; }
    public required string ModelName { get; init; }
    public required string Pretrained { get; init; }
    public required int FeatureDim { get; init; }
    public required int TopK { get; init; }
    public required float Threshold { get; init; }
    public required float TextWeight { get; init; }

    public static SqliteProductConfig FromCache(ClipCache cache)
    {
        return new SqliteProductConfig
        {
            ProductId = cache.ProductId,
            Name = cache.ProductId,
            ModelName = cache.ModelName,
            Pretrained = cache.Pretrained,
            FeatureDim = cache.FeatureDim,
            TopK = cache.TopK,
            Threshold = cache.Threshold,
            TextWeight = cache.TextWeight
        };
    }
}

public sealed class SqliteProductInfo
{
    public required string ProductId { get; init; }
    public string? Name { get; init; }
    public required string ModelName { get; init; }
    public required string Pretrained { get; init; }
    public required int FeatureDim { get; init; }
    public required int TopK { get; init; }
    public required float Threshold { get; init; }
    public required float TextWeight { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}

public sealed class SqliteVectorSample
{
    public required long Id { get; init; }
    public required string ProductId { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public string? ImagePath { get; init; }
    public string? Prompt { get; init; }
    public required float[] Feature { get; init; }
    public required bool Enabled { get; init; }
    public required string Source { get; init; }
    public string? Note { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}

public sealed class SqliteVectorSearchResult
{
    public required SqliteVectorSample Sample { get; init; }
    public required float Similarity { get; init; }
}
