using Microsoft.Win32;

namespace DebtFlow.SipAgent.Host;

public interface IStartupRegistrationManager
{
    bool IsEnabled { get; }
    void InitializeDefault();
    void SetEnabled(bool enabled);
}

public interface IUserStartupRegistry
{
    object? GetValue(string keyPath, string valueName);
    void SetString(string keyPath, string valueName, string value);
    void SetDword(string keyPath, string valueName, int value);
    void DeleteValue(string keyPath, string valueName);
}

public sealed class StartupRegistrationManager : IStartupRegistrationManager
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string PreferenceKeyPath = @"Software\DebtFlow\SipAgent";
    public const string RunValueName = "DebtFlowSipAgent";
    private readonly IUserStartupRegistry _registry;
    private readonly Func<string?> _processPath;

    public StartupRegistrationManager()
        : this(new WindowsUserStartupRegistry(), () => Environment.ProcessPath)
    {
    }

    public StartupRegistrationManager(IUserStartupRegistry registry, Func<string?> processPath)
    {
        _registry = registry;
        _processPath = processPath;
    }

    public bool IsEnabled =>
        _registry.GetValue(RunKeyPath, RunValueName) is string value &&
        string.Equals(value, StartupCommand(), StringComparison.Ordinal);

    public void InitializeDefault()
    {
        if (_registry.GetValue(PreferenceKeyPath, "StartupConfigured") is not int)
        {
            SetEnabled(true);
            return;
        }

        object? savedPreference = _registry.GetValue(PreferenceKeyPath, "StartWithWindows");
        bool enabled = savedPreference is int value
            ? value != 0
            : _registry.GetValue(RunKeyPath, RunValueName) is string;
        if (enabled)
        {
            _registry.SetString(RunKeyPath, RunValueName, StartupCommand());
        }
        else
        {
            _registry.DeleteValue(RunKeyPath, RunValueName);
        }

        _registry.SetDword(PreferenceKeyPath, "StartWithWindows", enabled ? 1 : 0);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _registry.SetString(RunKeyPath, RunValueName, StartupCommand());
        }
        else
        {
            _registry.DeleteValue(RunKeyPath, RunValueName);
        }

        _registry.SetDword(PreferenceKeyPath, "StartupConfigured", 1);
        _registry.SetDword(PreferenceKeyPath, "StartWithWindows", enabled ? 1 : 0);
    }

    private string StartupCommand()
    {
        string executable = _processPath() ?? throw new InvalidOperationException("process_path_unavailable");
        return $"\"{executable}\" --background";
    }
}

public sealed class WindowsUserStartupRegistry : IUserStartupRegistry
{
    public object? GetValue(string keyPath, string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName);
    }

    public void SetString(string keyPath, string valueName, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void SetDword(string keyPath, string valueName, int value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
