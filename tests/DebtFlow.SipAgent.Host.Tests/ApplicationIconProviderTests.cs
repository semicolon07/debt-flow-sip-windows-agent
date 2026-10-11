using System.Drawing;
using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class ApplicationIconProviderTests
{
    [Fact]
    public void Missing_executable_uses_an_owned_fallback_icon()
    {
        using Icon icon = ApplicationIconProvider.Load(Path.Combine(
            Path.GetTempPath(),
            $"missing-sip-agent-{Guid.NewGuid():N}.exe"));

        Assert.True(icon.Width > 0);
        Assert.True(icon.Height > 0);
    }

    [Fact]
    public void Approved_multiresolution_icon_is_loadable_by_windows()
    {
        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Branding",
            "DebtFlow.SipAgent.ico");

        Assert.True(File.Exists(iconPath), $"Application icon was not found: {iconPath}");
        using var icon = new Icon(iconPath);
        Assert.True(icon.Width > 0);
        Assert.True(icon.Height > 0);

        using FileStream stream = File.OpenRead(iconPath);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        ushort frameCount = reader.ReadUInt16();
        Assert.Equal(7, frameCount);

        var dimensions = new List<int>(frameCount);
        for (var index = 0; index < frameCount; index++)
        {
            byte width = reader.ReadByte();
            byte height = reader.ReadByte();
            dimensions.Add(width == 0 ? 256 : width);
            Assert.Equal(width, height);

            reader.ReadByte(); // Palette size; zero for true-color frames.
            Assert.Equal(0, reader.ReadByte());
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());

            uint payloadLength = reader.ReadUInt32();
            uint payloadOffset = reader.ReadUInt32();
            Assert.True(payloadLength > 0);
            Assert.InRange((long)payloadOffset + payloadLength, 1, stream.Length);
        }

        Assert.Equal([16, 24, 32, 48, 64, 128, 256], dimensions);
    }
}
