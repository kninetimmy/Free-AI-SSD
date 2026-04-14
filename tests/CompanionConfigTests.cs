using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Tests;

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
                SchemaVersion = 1
            };

            input.Save(path);
            var output = CompanionConfig.Load(path);

            Assert.Equal(input.HostAddress, output.HostAddress);
            Assert.Equal(input.PttBinding, output.PttBinding);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
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
}
