using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;

namespace DebtFlow.SipAgent.Core.Tests;

public sealed class FileAudioPreferencesStoreTests
{
    [Fact]
    public void MissingFile_ReturnsSafeDefaults()
    {
        string directory = CreateDirectory();
        try
        {
            var store = new FileAudioPreferencesStore(Path.Combine(directory, "audio-preferences.json"));

            Assert.Equal(AudioPreferences.Default, store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_RoundTripsAndReplacesPreviousPreferences()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "audio-preferences.json");
            var store = new FileAudioPreferencesStore(path);

            store.Save(new AudioPreferences(65, 40, "output-a", "input-a"));
            store.Save(new AudioPreferences(75, 55, "output-b", "input-b"));

            Assert.Equal(
                new AudioPreferences(75, 55, "output-b", "input-b"),
                new FileAudioPreferencesStore(path).Load());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SchemaVersionOne_MigratesVolumesAndUsesSystemDefaultDevices()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "audio-preferences.json");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"outputVolume\":65,\"inputVolume\":40}");

            Assert.Equal(
                new AudioPreferences(65, 40, "system-default", "system-default"),
                new FileAudioPreferencesStore(path).Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":3,\"outputVolume\":65,\"inputVolume\":40}")]
    [InlineData("{\"schemaVersion\":1,\"outputVolume\":101,\"inputVolume\":40}")]
    [InlineData("{\"schemaVersion\":1,\"outputVolume\":65,\"inputVolume\":40,\"unknown\":true}")]
    public void InvalidFile_ReturnsSafeDefaults(string content)
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "audio-preferences.json");
            File.WriteAllText(path, content);

            Assert.Equal(AudioPreferences.Default, new FileAudioPreferencesStore(path).Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_RejectsOutOfRangePreferences()
    {
        string directory = CreateDirectory();
        try
        {
            var store = new FileAudioPreferencesStore(Path.Combine(directory, "audio-preferences.json"));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                store.Save(new AudioPreferences(-1, 100, "system-default", "system-default")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
