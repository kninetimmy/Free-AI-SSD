using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Tests;

public sealed class PttBindingParserTests
{
    [Fact]
    public void ParseHotas_ValidBinding_ExtractsDeviceAndButton()
    {
        PttBindingParser.ParseHotas("Throttle|2", out var device, out var button);
        Assert.Equal("Throttle", device);
        Assert.Equal(2, button);
    }

    [Fact]
    public void ParseHotas_InvalidBinding_ReturnsNullDevice()
    {
        PttBindingParser.ParseHotas("badstuff", out var device, out _);
        Assert.Null(device);
    }

    [Fact]
    public void ParseHotas_EmptyBinding_ReturnsNullDevice()
    {
        PttBindingParser.ParseHotas("", out var device, out _);
        Assert.Null(device);
    }

    [Fact]
    public void ParseHotas_NullBinding_ReturnsNullDevice()
    {
        PttBindingParser.ParseHotas(null, out var device, out _);
        Assert.Null(device);
    }
}

public sealed class CompanionConfigTests
{
    [Fact]
    public void Load_ValidJson_Succeeds()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "hostAddress": "192.168.1.42",
              "hostPort": 41555,
              "apiKey": "abc",
              "pttBinding": "key:F8",
              "autoReconnect": true
            }
            """);

            var config = CompanionConfig.Load(path);
            Assert.Equal("192.168.1.42", config.HostAddress);
            Assert.Equal(41555, config.HostPort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var path = Path.GetTempFileName();
        File.Delete(path);
        try
        {
            var input = new CompanionConfig
            {
                HostAddress = "dcs-host",
                HostPort = 41555,
                ApiKey = "secret",
                PttBinding = "Throttle|2",
                InputDeviceName = "Mic",
                AutoReconnect = true,
                PttActivationSoundEnabled = false,
                PttOverlayEnabled = false,
                SchemaVersion = 1
            };

            input.Save(path);
            var output = CompanionConfig.Load(path);

            Assert.Equal(input.HostAddress, output.HostAddress);
            Assert.Equal(input.PttBinding, output.PttBinding);
            Assert.Equal(input.PttActivationSoundEnabled, output.PttActivationSoundEnabled);
            Assert.Equal(input.PttOverlayEnabled, output.PttOverlayEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NewConfig_HasVrUxDefaultsEnabled()
    {
        var config = new CompanionConfig();
        Assert.True(config.PttActivationSoundEnabled);
        Assert.True(config.PttOverlayEnabled);
    }

    [Fact]
    public void Load_RejectsUnsupportedSchema()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{" + "\"schemaVersion\":2}" );
            Assert.Throws<InvalidOperationException>(() => CompanionConfig.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ServerRequiresApiKey_DefaultsTrue()
    {
        Assert.True(new CompanionConfig().ServerRequiresApiKey);
    }

    [Fact]
    public void IsComplete_ReturnsFalse_WhenApiKeyRequiredAndBlank()
    {
        var config = new CompanionConfig
        {
            HostAddress = "192.168.1.1",
            HostPort = 41555,
            PttBinding = "key:F8",
            ApiKey = "",
            ServerRequiresApiKey = true,
        };
        Assert.False(config.IsComplete());
    }

    [Fact]
    public void IsComplete_ReturnsTrue_WhenApiKeyNotRequired()
    {
        var config = new CompanionConfig
        {
            HostAddress = "192.168.1.1",
            HostPort = 41555,
            PttBinding = "key:F8",
            ApiKey = "",
            ServerRequiresApiKey = false,
        };
        Assert.True(config.IsComplete());
    }

    [Fact]
    public void IsComplete_ReturnsFalse_WhenPttBindingBlank()
    {
        var config = new CompanionConfig
        {
            HostAddress = "192.168.1.1",
            HostPort = 41555,
            PttBinding = "",
            ApiKey = "secret",
        };
        Assert.False(config.IsComplete());
    }
}
