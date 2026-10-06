using System.Text.Json;
using System.Text.Json.Serialization;
using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Persistence;

public sealed class FileAudioPreferencesStore(string path) : IAudioPreferencesStore
{
    private const int SchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    private readonly object _gate = new();

    public AudioPreferences Load()
    {
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return AudioPreferences.Default;
            }

            try
            {
                AudioPreferencesDocument? document = JsonSerializer.Deserialize<AudioPreferencesDocument>(
                    File.ReadAllBytes(path),
                    JsonOptions);
                if (document is not
                    {
                        SchemaVersion: 1 or SchemaVersion,
                        OutputVolume: >= 0 and <= 100,
                        InputVolume: >= 0 and <= 100
                    })
                {
                    return AudioPreferences.Default;
                }

                return new AudioPreferences(
                    document.OutputVolume,
                    document.InputVolume,
                    NormalizeDeviceId(document.OutputDeviceId),
                    NormalizeDeviceId(document.InputDeviceId));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return AudioPreferences.Default;
            }
        }
    }

    public void Save(AudioPreferences preferences)
    {
        Validate(preferences);
        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("audio_preferences_path_invalid");
            }

            Directory.CreateDirectory(directory);
            string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                var document = new AudioPreferencesDocument(
                    SchemaVersion,
                    preferences.OutputVolume,
                    preferences.InputVolume,
                    preferences.OutputDeviceId,
                    preferences.InputDeviceId);
                File.WriteAllBytes(
                    temporaryPath,
                    JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static void Validate(AudioPreferences preferences)
    {
        if (preferences.OutputVolume is < 0 or > 100 ||
            preferences.InputVolume is < 0 or > 100 ||
            string.IsNullOrWhiteSpace(preferences.OutputDeviceId) ||
            string.IsNullOrWhiteSpace(preferences.InputDeviceId) ||
            preferences.OutputDeviceId.Length > 128 ||
            preferences.InputDeviceId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(preferences));
        }
    }

    private sealed record AudioPreferencesDocument(
        int SchemaVersion,
        int OutputVolume,
        int InputVolume,
        string? OutputDeviceId = null,
        string? InputDeviceId = null);

    private static string NormalizeDeviceId(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 128
            ? AudioPreferences.SystemDefaultDeviceId
            : value;
}
