using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace DebtFlow.SipAgent.Host;

internal sealed class AudioSessionVolumeController
{
    private readonly object _gate = new();
    private readonly Dictionary<DataFlow, string> _preferredDeviceIds = new();

    public void Invalidate()
    {
        lock (_gate)
        {
            _preferredDeviceIds.Clear();
        }
    }

    public bool TrySetProcessVolume(DataFlow dataFlow, int volume)
    {
        float scalar = Math.Clamp(volume, 0, 100) / 100f;
        int processId = Environment.ProcessId;

        using var enumerator = new MMDeviceEnumerator();
        string? preferredDeviceId;
        lock (_gate)
        {
            _preferredDeviceIds.TryGetValue(dataFlow, out preferredDeviceId);
        }

        if (preferredDeviceId != null)
        {
            try
            {
                using MMDevice preferred = enumerator.GetDevice(preferredDeviceId);
                if (TrySetDeviceVolume(preferred, processId, scalar))
                {
                    return true;
                }
            }
            catch (COMException)
            {
            }

            lock (_gate)
            {
                _preferredDeviceIds.Remove(dataFlow);
            }
        }

        using MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
        for (int deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            using MMDevice device = devices[deviceIndex];
            if (!TrySetDeviceVolume(device, processId, scalar))
            {
                continue;
            }

            lock (_gate)
            {
                _preferredDeviceIds[dataFlow] = device.ID;
            }
            return true;
        }

        return false;
    }

    private static bool TrySetDeviceVolume(MMDevice device, int processId, float scalar)
    {
        bool applied = false;
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

        return applied;
    }
}
