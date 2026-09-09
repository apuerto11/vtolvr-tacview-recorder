using System;
using System.Threading.Tasks;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class TacviewTerrainCacheProbe
{
    internal TacviewTerrainCacheProbe(bool isCurrent, string error)
    {
        IsCurrent = isCurrent;
        Error = error;
    }

    public bool IsCurrent { get; }

    public string Error { get; }
}

public sealed class TacviewTerrainPayload
{
    private readonly byte[] _rawBytes;

    public TacviewTerrainPayload(TacviewTerrainDefinition definition, byte[] rawBytes)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        if (rawBytes == null || rawBytes.LongLength != definition.ExpectedRawLength)
        {
            throw new ArgumentException("RAW byte count does not match the terrain dimensions.", nameof(rawBytes));
        }

        _rawBytes = new byte[rawBytes.Length];
        Buffer.BlockCopy(rawBytes, 0, _rawBytes, 0, rawBytes.Length);
    }

    public TacviewTerrainDefinition Definition { get; }

    internal TacviewTerrainInstallResult Install(TacviewTerrainCache cache)
    {
        return cache.TryInstall(Definition, _rawBytes);
    }
}

public static class TacviewTerrainBackground
{
    // This Core-only type is the sole background-thread boundary for terrain.
    // The Core test project compiles without Unity, Assembly-CSharp, or VTOLAPI.
    public static Task<TacviewTerrainCacheProbe> CheckCacheAsync(
        TacviewTerrainCache cache,
        TacviewTerrainDefinition definition)
    {
        if (cache == null)
        {
            throw new ArgumentNullException(nameof(cache));
        }

        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        return Task.Run(() =>
        {
            try
            {
                return new TacviewTerrainCacheProbe(cache.IsCurrent(definition), null);
            }
            catch (Exception ex)
            {
                return new TacviewTerrainCacheProbe(
                    false,
                    ex.GetType().Name + ": " + ex.Message);
            }
        });
    }

    public static Task<TacviewTerrainInstallResult> InstallAsync(
        TacviewTerrainCache cache,
        TacviewTerrainPayload payload)
    {
        if (cache == null)
        {
            throw new ArgumentNullException(nameof(cache));
        }

        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        return Task.Run(() => payload.Install(cache));
    }
}
