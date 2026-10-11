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
        using var icon = new Icon(iconPath, 256, 256);
        Assert.Equal(256, icon.Width);
        Assert.Equal(256, icon.Height);

        using FileStream stream = File.OpenRead(iconPath);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        Assert.Equal(7, reader.ReadUInt16());
    }
}
