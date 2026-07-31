using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RoiAlignment.Core;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public sealed class AlignmentTemplateStore
{
    private const int SchemaVersion = 1;

    public AlignmentTemplateStore(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; set; }

    public AlignmentTemplateRecord? Load(string productModelId, string cameraId)
    {
        Initialize();
        using var connection = OpenConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select product_model_id, camera_id, reference_image_relative_path,
                   image_width, image_height, processing_scale,
                   feature_method, transform_model, max_long_side, max_features,
                   lowe_ratio, min_good_matches, min_inliers, min_inlier_ratio,
                   ransac_reprojection_threshold, max_reprojection_rmse,
                   keypoints_json, descriptor_rows, descriptor_cols, descriptor_mat_type,
                   descriptor_blob, created_at, updated_at
            from alignment_templates
            where product_model_id = $product_model_id and camera_id = $camera_id;
            """;
        command.Parameters.AddWithValue("$product_model_id", productModelId);
        command.Parameters.AddWithValue("$camera_id", cameraId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public void Save(
        CameraAlignmentDefinition definition,
        AlignmentTemplate template)
    {
        Initialize();
        using var connection = OpenConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToString("O");
        command.CommandText = """
            insert into alignment_templates(
                product_model_id, camera_id, reference_image_relative_path,
                image_width, image_height, processing_scale,
                feature_method, transform_model, max_long_side, max_features,
                lowe_ratio, min_good_matches, min_inliers, min_inlier_ratio,
                ransac_reprojection_threshold, max_reprojection_rmse,
                keypoints_json, descriptor_rows, descriptor_cols, descriptor_mat_type,
                descriptor_blob, created_at, updated_at, schema_version)
            values(
                $product_model_id, $camera_id, $reference_image_relative_path,
                $image_width, $image_height, $processing_scale,
                $feature_method, $transform_model, $max_long_side, $max_features,
                $lowe_ratio, $min_good_matches, $min_inliers, $min_inlier_ratio,
                $ransac_reprojection_threshold, $max_reprojection_rmse,
                $keypoints_json, $descriptor_rows, $descriptor_cols, $descriptor_mat_type,
                $descriptor_blob, $created_at, $updated_at, $schema_version)
            on conflict(product_model_id, camera_id) do update set
                reference_image_relative_path = excluded.reference_image_relative_path,
                image_width = excluded.image_width,
                image_height = excluded.image_height,
                processing_scale = excluded.processing_scale,
                feature_method = excluded.feature_method,
                transform_model = excluded.transform_model,
                max_long_side = excluded.max_long_side,
                max_features = excluded.max_features,
                lowe_ratio = excluded.lowe_ratio,
                min_good_matches = excluded.min_good_matches,
                min_inliers = excluded.min_inliers,
                min_inlier_ratio = excluded.min_inlier_ratio,
                ransac_reprojection_threshold = excluded.ransac_reprojection_threshold,
                max_reprojection_rmse = excluded.max_reprojection_rmse,
                keypoints_json = excluded.keypoints_json,
                descriptor_rows = excluded.descriptor_rows,
                descriptor_cols = excluded.descriptor_cols,
                descriptor_mat_type = excluded.descriptor_mat_type,
                descriptor_blob = excluded.descriptor_blob,
                updated_at = excluded.updated_at,
                schema_version = excluded.schema_version;
            """;
        command.Parameters.AddWithValue("$product_model_id", definition.ProductModelId);
        command.Parameters.AddWithValue("$camera_id", definition.CameraId);
        command.Parameters.AddWithValue("$reference_image_relative_path", definition.ReferenceImageRelativePath);
        command.Parameters.AddWithValue("$image_width", template.ImageWidth);
        command.Parameters.AddWithValue("$image_height", template.ImageHeight);
        command.Parameters.AddWithValue("$processing_scale", template.ProcessingScale);
        command.Parameters.AddWithValue("$feature_method", template.FeatureMethod.ToString());
        command.Parameters.AddWithValue("$transform_model", template.TransformModel.ToString());
        command.Parameters.AddWithValue("$max_long_side", definition.MaxLongSide);
        command.Parameters.AddWithValue("$max_features", definition.MaxFeatures);
        command.Parameters.AddWithValue("$lowe_ratio", definition.LoweRatio);
        command.Parameters.AddWithValue("$min_good_matches", definition.MinGoodMatches);
        command.Parameters.AddWithValue("$min_inliers", definition.MinInliers);
        command.Parameters.AddWithValue("$min_inlier_ratio", definition.MinInlierRatio);
        command.Parameters.AddWithValue("$ransac_reprojection_threshold", definition.RansacReprojectionThreshold);
        command.Parameters.AddWithValue("$max_reprojection_rmse", definition.MaxReprojectionRmse);
        command.Parameters.AddWithValue("$keypoints_json", JsonSerializer.Serialize(template.KeyPoints, AlignmentTemplate.JsonOptions));
        command.Parameters.AddWithValue("$descriptor_rows", template.Descriptors.Rows);
        command.Parameters.AddWithValue("$descriptor_cols", template.Descriptors.Cols);
        command.Parameters.AddWithValue("$descriptor_mat_type", template.Descriptors.MatType);
        command.Parameters.Add("$descriptor_blob", SqliteType.Blob).Value = Convert.FromBase64String(template.Descriptors.DataBase64);
        command.Parameters.AddWithValue("$created_at", definition.RegisteredAt?.ToUniversalTime().ToString("O") ?? now);
        command.Parameters.AddWithValue("$updated_at", now);
        command.Parameters.AddWithValue("$schema_version", SchemaVersion);
        command.ExecuteNonQuery();
    }

    public void Delete(string productModelId, string cameraId)
    {
        Initialize();
        using var connection = OpenConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            delete from alignment_templates
            where product_model_id = $product_model_id and camera_id = $camera_id;
            """;
        command.Parameters.AddWithValue("$product_model_id", productModelId);
        command.Parameters.AddWithValue("$camera_id", cameraId);
        command.ExecuteNonQuery();
    }

    public void DeleteProduct(string productModelId)
    {
        Initialize();
        using var connection = OpenConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "delete from alignment_templates where product_model_id = $product_model_id;";
        command.Parameters.AddWithValue("$product_model_id", productModelId);
        command.ExecuteNonQuery();
    }

    public void CopyProduct(string sourceProductId, string targetProductId)
    {
        Initialize();
        using var connection = OpenConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert or replace into alignment_templates(
                product_model_id, camera_id, reference_image_relative_path,
                image_width, image_height, processing_scale,
                feature_method, transform_model, max_long_side, max_features,
                lowe_ratio, min_good_matches, min_inliers, min_inlier_ratio,
                ransac_reprojection_threshold, max_reprojection_rmse,
                keypoints_json, descriptor_rows, descriptor_cols, descriptor_mat_type,
                descriptor_blob, created_at, updated_at, schema_version)
            select
                $target_product_id, camera_id,
                replace(
                    replace(reference_image_relative_path, $source_segment_slash, $target_segment_slash),
                    $source_segment_backslash,
                    $target_segment_backslash),
                image_width, image_height, processing_scale,
                feature_method, transform_model, max_long_side, max_features,
                lowe_ratio, min_good_matches, min_inliers, min_inlier_ratio,
                ransac_reprojection_threshold, max_reprojection_rmse,
                keypoints_json, descriptor_rows, descriptor_cols, descriptor_mat_type,
                descriptor_blob, created_at, $updated_at, schema_version
            from alignment_templates
            where product_model_id = $source_product_id;
            """;
        command.Parameters.AddWithValue("$source_product_id", sourceProductId);
        command.Parameters.AddWithValue("$target_product_id", targetProductId);
        command.Parameters.AddWithValue("$source_segment_slash", $"Products/{sourceProductId}");
        command.Parameters.AddWithValue("$target_segment_slash", $"Products/{targetProductId}");
        command.Parameters.AddWithValue("$source_segment_backslash", $"Products\\{sourceProductId}");
        command.Parameters.AddWithValue("$target_segment_backslash", $"Products\\{targetProductId}");
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists alignment_templates (
                product_model_id text not null,
                camera_id text not null,
                reference_image_relative_path text not null,
                image_width integer not null,
                image_height integer not null,
                processing_scale real not null,
                feature_method text not null,
                transform_model text not null,
                max_long_side integer not null,
                max_features integer not null,
                lowe_ratio real not null,
                min_good_matches integer not null,
                min_inliers integer not null,
                min_inlier_ratio real not null,
                ransac_reprojection_threshold real not null,
                max_reprojection_rmse real not null,
                keypoints_json text not null,
                descriptor_rows integer not null,
                descriptor_cols integer not null,
                descriptor_mat_type integer not null,
                descriptor_blob blob not null,
                created_at text not null,
                updated_at text not null,
                schema_version integer not null,
                primary key(product_model_id, camera_id)
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection() => new($"Data Source={DatabasePath}");

    private static AlignmentTemplateRecord ReadRecord(SqliteDataReader reader)
    {
        var keyPoints = JsonSerializer.Deserialize<KeyPointDto[]>(reader.GetString(16), AlignmentTemplate.JsonOptions) ?? [];
        var descriptors = new DescriptorData
        {
            Rows = reader.GetInt32(17),
            Cols = reader.GetInt32(18),
            MatType = reader.GetInt32(19),
            DataBase64 = Convert.ToBase64String((byte[])reader["descriptor_blob"])
        };
        var template = new AlignmentTemplate
        {
            ImageWidth = reader.GetInt32(3),
            ImageHeight = reader.GetInt32(4),
            ProcessingScale = reader.GetDouble(5),
            FeatureMethod = ParseEnum(reader.GetString(6), FeatureMethod.Sift),
            TransformModel = ParseEnum(reader.GetString(7), TransformModel.AffinePartial),
            MaxLongSide = reader.GetInt32(8),
            MaxFeatures = reader.GetInt32(9),
            KeyPoints = keyPoints,
            Descriptors = descriptors,
            Metadata = new TemplateMetadata
            {
                CreatedAt = DateTimeOffset.TryParse(reader.GetString(21), out var createdAt)
                    ? createdAt
                    : DateTimeOffset.UtcNow
            }
        };

        return new AlignmentTemplateRecord
        {
            ProductModelId = reader.GetString(0),
            CameraId = reader.GetString(1),
            ReferenceImageRelativePath = reader.GetString(2),
            Template = template,
            Options = new AlignmentOptions
            {
                FeatureMethod = template.FeatureMethod,
                TransformModel = template.TransformModel,
                MaxLongSide = template.MaxLongSide,
                MaxFeatures = template.MaxFeatures,
                LoweRatio = reader.GetDouble(10),
                MinGoodMatches = reader.GetInt32(11),
                MinInliers = reader.GetInt32(12),
                MinInlierRatio = reader.GetDouble(13),
                RansacReprojectionThreshold = reader.GetDouble(14),
                MaxReprojectionRmse = reader.GetDouble(15)
            },
            UpdatedAt = DateTimeOffset.TryParse(reader.GetString(22), out var updatedAt)
                ? updatedAt
                : DateTimeOffset.UtcNow
        };
    }

    private static T ParseEnum<T>(string value, T fallback)
        where T : struct
    {
        return Enum.TryParse<T>(value, ignoreCase: true, out var result) ? result : fallback;
    }
}

public sealed class AlignmentTemplateRecord
{
    public required string ProductModelId { get; init; }

    public required string CameraId { get; init; }

    public required string ReferenceImageRelativePath { get; init; }

    public required AlignmentTemplate Template { get; init; }

    public required AlignmentOptions Options { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
