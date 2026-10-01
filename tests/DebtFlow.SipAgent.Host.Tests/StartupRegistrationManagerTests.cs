using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class StartupRegistrationManagerTests
{
    [Fact]
    public void FirstLaunch_EnablesQuotedBackgroundCommand()
    {
        var registry = new FakeRegistry();
        var manager = new StartupRegistrationManager(registry, () => @"C:\Program Files\Debt Flow\Agent.exe");

        manager.InitializeDefault();

        Assert.True(manager.IsEnabled);
        Assert.Equal(
            "\"C:\\Program Files\\Debt Flow\\Agent.exe\" --background",
            registry.GetValue(StartupRegistrationManager.RunKeyPath, StartupRegistrationManager.RunValueName));
    }

    [Fact]
    public void ExplicitDisable_IsRememberedAndNotReenabledOnNextLaunch()
    {
        var registry = new FakeRegistry();
        var manager = new StartupRegistrationManager(registry, () => @"C:\Agent.exe");
        manager.InitializeDefault();

        manager.SetEnabled(false);
        manager.InitializeDefault();

        Assert.False(manager.IsEnabled);
        Assert.Null(registry.GetValue(StartupRegistrationManager.RunKeyPath, StartupRegistrationManager.RunValueName));
    }

    [Fact]
    public void EnabledPreference_ReconcilesRunCommandAfterExecutablePathChanges()
    {
        var registry = new FakeRegistry();
        var previous = new StartupRegistrationManager(registry, () => @"C:\Old Folder\Agent.exe");
        previous.SetEnabled(true);
        var current = new StartupRegistrationManager(registry, () => @"C:\Program Files\Debt Flow\Agent.exe");

        current.InitializeDefault();

        Assert.True(current.IsEnabled);
        Assert.Equal(
            "\"C:\\Program Files\\Debt Flow\\Agent.exe\" --background",
            registry.GetValue(StartupRegistrationManager.RunKeyPath, StartupRegistrationManager.RunValueName));
    }

    [Fact]
    public void DisabledPreference_RemovesExternallyReintroducedRunCommand()
    {
        var registry = new FakeRegistry();
        var manager = new StartupRegistrationManager(registry, () => @"C:\Agent.exe");
        manager.SetEnabled(false);
        registry.SetString(
            StartupRegistrationManager.RunKeyPath,
            StartupRegistrationManager.RunValueName,
            "\"C:\\Stale\\Agent.exe\" --background");

        manager.InitializeDefault();

        Assert.False(manager.IsEnabled);
        Assert.Null(registry.GetValue(StartupRegistrationManager.RunKeyPath, StartupRegistrationManager.RunValueName));
    }

    private sealed class FakeRegistry : IUserStartupRegistry
    {
        private readonly Dictionary<(string Path, string Name), object> _values = [];

        public object? GetValue(string keyPath, string valueName) =>
            _values.GetValueOrDefault((keyPath, valueName));

        public void SetString(string keyPath, string valueName, string value) =>
            _values[(keyPath, valueName)] = value;

        public void SetDword(string keyPath, string valueName, int value) =>
            _values[(keyPath, valueName)] = value;

        public void DeleteValue(string keyPath, string valueName) =>
            _values.Remove((keyPath, valueName));
    }
}
