using System.Net;
using System.Security.Cryptography;

namespace FreeAiSsd.Shared.Services;

/// <summary>
/// Single chokepoint for portable-config reads and writes. Serializes every
/// save via an internal semaphore (prevents overlapping <c>File.Replace</c>
/// calls), caches the <see cref="UnlockMaterial"/> while a session is unlocked
/// so encrypted saves skip PBKDF2, and enforces the Network-Mode + RequireApiKey
/// fail-closed guard previously lived on <see cref="PortableConfig.SaveAsync"/>.
///
/// Stage 2 delivery: not wired into Runner or Prep yet. Stages 3-4 replace
/// direct <see cref="PortableConfig.SaveAsync"/> callers with this store.
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    private readonly SsdLogger? _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _materialLock = new();
    private UnlockMaterial? _material;

    public ConfigStore(SsdLogger? logger = null)
    {
        _logger = logger;
    }

    public bool IsSessionUnlocked
    {
        get
        {
            lock (_materialLock)
            {
                return _material is not null;
            }
        }
    }

    public void UnlockSession(UnlockMaterial material)
    {
        if (material is null) throw new ArgumentNullException(nameof(material));

        lock (_materialLock)
        {
            // Zero any previously cached key before replacing — never leak a stale key.
            if (_material is not null)
            {
                CryptographicOperations.ZeroMemory(_material.DerivedKey);
            }
            _material = material;
        }
    }

    public void LockSession()
    {
        lock (_materialLock)
        {
            if (_material is not null)
            {
                CryptographicOperations.ZeroMemory(_material.DerivedKey);
                _material = null;
            }
        }
    }

    public async Task<PortableConfig?> LoadAsync(string ssdRoot, CancellationToken ct)
    {
        var configPath = Path.Combine(ssdRoot, SsdLayout.Config, "portable-config.json");

        if (SsdEncryption.IsEffectivelyEncryptedForWriteGuard(ssdRoot))
        {
            // Encrypted drives are loaded via SsdEncryption.TryUnlockPortableConfigWithMaterial
            // by the caller, which also hands us the material via UnlockSession. Returning null
            // here signals "encrypted, not unlocked" to Stage 3 consumers.
            return null;
        }

        if (!File.Exists(configPath))
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        return await PortableConfig.LoadAsync(configPath).ConfigureAwait(false);
    }

    public async Task SaveAsync(string ssdRoot, PortableConfig config, CancellationToken ct)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        var isEncrypted = SsdEncryption.IsEffectivelyEncryptedForWriteGuard(ssdRoot);

        // Fail-closed: Network Mode + RequireApiKey on an unencrypted drive means
        // the shared-secret API key would be written as plaintext. Refuse.
        // Mirrors the existing axis from PortableConfigSaveGuardTests.
        if (config.NetworkModeEnabled && config.NetworkRequireApiKey && !isEncrypted)
        {
            throw new InvalidOperationException(PortableConfig.NetworkModeEncryptionRequiredMessage);
        }

        // #114: a non-loopback (LAN-reachable) bind with no API key would serve the
        // Runner API unauthenticated, and RunnerLocalApiService now refuses to start
        // such a host. Refuse to persist that config so the UI can't save the drive
        // into an unstartable / wide-open state. Independent of encryption — the
        // exposure is a runtime property of the bind, not of how config is stored.
        if (config.NetworkModeEnabled
            && IsNonLoopbackBind(config.NetworkBindAddress)
            && string.IsNullOrWhiteSpace(config.NetworkApiKey))
        {
            throw new InvalidOperationException(PortableConfig.NetworkApiKeyRequiredForLanMessage);
        }

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (isEncrypted)
            {
                UnlockMaterial? material;
                lock (_materialLock)
                {
                    material = _material;
                }

                if (material is null)
                {
                    // Drive is encrypted but no session is unlocked. We cannot safely
                    // persist without a key; surfacing this as an exception lets Stage 3
                    // callers show the unlock dialog rather than silently dropping the save.
                    throw new InvalidOperationException(
                        "SSD config is encrypted but the session is locked. Unlock the drive before saving.");
                }

                await SsdEncryption.SaveEncryptedConfigAsync(ssdRoot, config, material, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var configPath = Path.Combine(ssdRoot, SsdLayout.Config, "portable-config.json");
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                await config.SaveAsync(configPath).ConfigureAwait(false);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task FlushAsync(TimeSpan timeout)
    {
        // Drain by acquiring and immediately releasing the save gate. If a save is
        // in flight, the wait returns only after it completes. Bounded by the caller's
        // timeout so shutdown can't hang forever on a stuck save.
        var acquired = false;
        try
        {
            acquired = await _saveGate.WaitAsync(timeout).ConfigureAwait(false);
            if (!acquired)
            {
                _logger?.Warn($"ConfigStore.FlushAsync timed out after {timeout.TotalSeconds:F1}s; a save is still in progress.");
            }
        }
        catch (Exception ex)
        {
            // Shutdown path: never throw.
            _logger?.Error($"ConfigStore.FlushAsync error: {ex.Message}");
        }
        finally
        {
            if (acquired)
            {
                _saveGate.Release();
            }
        }
    }

    /// <summary>
    /// True only when <paramref name="address"/> parses to a non-loopback IP. A blank or
    /// unparseable value is treated as loopback here: the runtime normalizes blank to
    /// 127.0.0.1, and an unparseable address is rejected loudly by StartAsync — neither is
    /// the LAN-reachable case the #114 write guard targets.
    /// </summary>
    private static bool IsNonLoopbackBind(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        return IPAddress.TryParse(address.Trim(), out var ip) && !IPAddress.IsLoopback(ip);
    }
}
