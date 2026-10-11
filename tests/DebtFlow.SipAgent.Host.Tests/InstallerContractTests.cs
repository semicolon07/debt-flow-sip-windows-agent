namespace DebtFlow.SipAgent.Host.Tests;

public sealed class InstallerContractTests
{
    private const string FixedAppId = "{476433C5-E4DF-4CD6-9E42-29F01E4BC339}";

    [Fact]
    public void Inno_script_preserves_fixed_per_user_safety_contract()
    {
        string script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Installer",
            "DebtFlow.SipAgent.iss"));

        Assert.Contains($"AppId={{{FixedAppId}", script, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", script, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequiredOverridesAllowed=", script, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\DebtFlow\\SipAgent", script, StringComparison.Ordinal);
        Assert.Contains("ArchitecturesAllowed=x64os", script, StringComparison.Ordinal);
        Assert.Contains("MinVersion=10.0.17763", script, StringComparison.Ordinal);
        Assert.Contains("CloseApplications=no", script, StringComparison.Ordinal);
        Assert.Contains("RestartApplications=no", script, StringComparison.Ordinal);
        Assert.Contains("skipifsilent", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Inno_script_has_no_force_kill_or_reboot_replacement_path()
    {
        string script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Installer",
            "DebtFlow.SipAgent.iss"));

        Assert.DoesNotContain("taskkill", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Process.Kill", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("restartreplace", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--prepare-update", script, StringComparison.Ordinal);
        Assert.Contains("UpdateActiveCall = 30", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Inno_script_uses_unambiguous_semantic_version_identifiers()
    {
        string script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Installer",
            "DebtFlow.SipAgent.iss"));

        Assert.DoesNotContain("LM, Lm", script, StringComparison.Ordinal);
        Assert.DoesNotContain("RM, Rm", script, StringComparison.Ordinal);
        Assert.Contains("LeftMajor, LeftMinor, LeftPatch", script, StringComparison.Ordinal);
        Assert.Contains("RightMajor, RightMinor, RightPatch", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_build_reads_the_canonical_compiler_engine_version()
    {
        string script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Scripts",
            "build-installer.ps1"));

        Assert.Contains("ArgumentList.Add(\"--version\")", script, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(10000)", script, StringComparison.Ordinal);
        Assert.Contains("inno_compiler_version_command_failed", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$iscc.VersionInfo.ProductVersion", script, StringComparison.Ordinal);
        Assert.Contains("version = $innoVersion", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("Get-AuthenticodeSignature -LiteralPath $iscc.FullName", StringComparison.Ordinal) <
            script.IndexOf("$innoVersion = Get-InnoCompilerVersion $iscc.FullName", StringComparison.Ordinal),
            "ISCC trust must be verified before executing its canonical version command.");
    }
}
