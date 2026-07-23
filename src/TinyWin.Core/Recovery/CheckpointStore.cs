using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyWin.Core.Recovery;

/// <summary>Where the per-stage checkpoint of docs/PLAN.md section 2.2 is kept.</summary>
public interface ICheckpointStore
{
    /// <summary>The most recent checkpoint, or null when there is none to resume.</summary>
    Task<BuildCheckpoint?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(BuildCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Removes the checkpoint. Called once a build finishes, so nothing stale is resumable.</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>state.json</c> in the scratch directory.
/// </summary>
/// <remarks>
/// Written to a temporary file and moved into place, because the one moment this file matters is
/// the moment the process dies — and a checkpoint truncated mid-write is worse than no checkpoint
/// at all. A corrupt or older-schema file is treated as "no checkpoint" rather than as a fatal
/// error: the build can always start over, and refusing to run because a recovery file is
/// unreadable would be the wrong trade.
/// </remarks>
public sealed class JsonCheckpointStore(string scratchDirectory) : ICheckpointStore
{
    public const string FileName = "state.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path = Path.Combine(scratchDirectory, FileName);

    /// <summary>Full path of the checkpoint file, for messages that tell the user to delete it.</summary>
    public string FilePath => _path;

    public async Task<BuildCheckpoint?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var checkpoint = await JsonSerializer
                .DeserializeAsync<BuildCheckpoint>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            return checkpoint?.SchemaVersion == BuildCheckpoint.CurrentSchemaVersion ? checkpoint : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(BuildCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        Directory.CreateDirectory(scratchDirectory);
        var temporary = _path + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer
                .SerializeAsync(stream, checkpoint, Options, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A checkpoint that will not delete is harmless: the fingerprint check refuses to
            // resume it onto a different build, and the next run overwrites it.
        }

        return Task.CompletedTask;
    }
}
