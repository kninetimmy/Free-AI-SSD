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
