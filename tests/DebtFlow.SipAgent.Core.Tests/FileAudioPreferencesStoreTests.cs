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

            store.Save(new AudioPreferences(65, 40));
            store.Save(new AudioPreferences(75, 55));

            Assert.Equal(new AudioPreferences(75, 55), new FileAudioPreferencesStore(path).Load());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":2,\"outputVolume\":65,\"inputVolume\":40}")]
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
                store.Save(new AudioPreferences(-1, 100)));
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
