namespace DebtFlow.SipAgent.Host;

public enum AgentLaunchCommand
{
    Run,
    RepairLocalCertificate,
    RemoveLocalCertificate,
    StoragePreflight,
    PrintReleaseMetadata
}

public static class AgentCommandLine
{
    public static AgentLaunchCommand Parse(string[] args)
    {
        var commands = new Dictionary<string, AgentLaunchCommand>(StringComparer.Ordinal)
        {
            ["--repair-local-certificate"] = AgentLaunchCommand.RepairLocalCertificate,
            ["--remove-local-certificate"] = AgentLaunchCommand.RemoveLocalCertificate,
            ["--storage-preflight"] = AgentLaunchCommand.StoragePreflight,
            ["--print-release-metadata"] = AgentLaunchCommand.PrintReleaseMetadata
        };
        string[] selected = args.Where(commands.ContainsKey).ToArray();
        if (selected.Length == 0) return AgentLaunchCommand.Run;

        if (selected.Length != 1 || args.Length != 1)
        {
            throw new AgentConfigurationException("maintenance_arguments_invalid");
        }

        return commands[selected[0]];
    }
}
