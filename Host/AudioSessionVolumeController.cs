using NAudio.CoreAudioApi;

namespace DebtFlow.SipAgent.Host;

internal sealed class AudioSessionVolumeController
{
    public bool TrySetProcessVolume(DataFlow dataFlow, int volume)
    {
        float scalar = Math.Clamp(volume, 0, 100) / 100f;
        int processId = Environment.ProcessId;
        bool applied = false;

        using var enumerator = new MMDeviceEnumerator();
        using MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
        for (int deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            using MMDevice device = devices[deviceIndex];
            using AudioSessionManager manager = device.AudioSessionManager;
            manager.RefreshSessions();
            using SessionCollection sessions = manager.Sessions;
            for (int sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
            {
                using AudioSessionControl session = sessions[sessionIndex];
                if (session.GetProcessID != processId)
                {
                    continue;
                }

                session.SimpleAudioVolume.Volume = scalar;
                applied = true;
            }
        }

        return applied;
    }
}
