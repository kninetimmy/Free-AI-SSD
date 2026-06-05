namespace FreeAiSsd.Shared;

public static class ProfileDefaults
{
    public static void Apply(PortableConfig config, UserProfile profile)
    {
        switch (profile)
        {
            case UserProfile.FlightSim:
                config.PttEnabled = true;
                config.TtsEnabled = true;
                config.AutoSendVoiceInput = true;
                config.PttActivationSoundEnabled = true;
                config.PttOverlayEnabled = true;
                // The VR companion's only protocol is POST /api/voice/query, which
                // is gated off by default. A FlightSim drive bundles the companion,
                // so the runner must accept remote voice queries or the companion
                // 403s out of the box. (Remote raw-STT stays off — voice/query is
                // the single endpoint the companion uses.)
                config.NetworkAllowRemoteVoiceQuery = true;
                break;

            case UserProfile.GeneralAssistant:
                config.PttEnabled = false;
                config.TtsEnabled = false;
                config.AutoSendVoiceInput = false;
                config.PttActivationSoundEnabled = false;
                config.PttOverlayEnabled = false;
                break;
        }
    }
}
