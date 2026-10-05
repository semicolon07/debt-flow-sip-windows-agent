namespace DebtFlow.SipAgent.Host;

public enum AgentLaunchCommand
{
    Run,
    RepairLocalCertificate,
    RemoveLocalCertificate
}

public static class AgentCommandLine
{
    public static AgentLaunchCommand Parse(string[] args)
    {
        bool repair = args.Contains("--repair-local-certificate", StringComparer.Ordinal);
        bool remove = args.Contains("--remove-local-certificate", StringComparer.Ordinal);
        if (!repair && !remove) return AgentLaunchCommand.Run;

        if (repair == remove || args.Length != 1)
        {
            throw new AgentConfigurationException("maintenance_arguments_invalid");
        }

        return repair ? AgentLaunchCommand.RepairLocalCertificate : AgentLaunchCommand.RemoveLocalCertificate;
    }
}
