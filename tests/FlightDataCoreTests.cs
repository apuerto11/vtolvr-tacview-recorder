using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class FlightDataCoreTests
{
    private static readonly DateTimeOffset ReferenceTime = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

    [Fact]
    public void EncoderWritesEverySetPropertyFirstTimeWithInvariantFormat()
    {
        var builder = new StringBuilder();
        TacviewTelemetry sample = new TacviewTelemetry()
            .Set(TacviewTelemetryProperty.IAS, 150.04)
            .Set(TacviewTelemetryProperty.TAS, 171.26)
            .Set(TacviewTelemetryProperty.Mach, 0.5123)
            .Set(TacviewTelemetryProperty.AOA, -2.25)
            .Set(TacviewTelemetryProperty.FuelVolume, 1234.6)
            .Set(TacviewTelemetryProperty.AGL, double.NaN);

        TacviewTelemetry state = TacviewTelemetryEncoder.AppendChanges(builder, sample, null, null);

        Assert.Equal(",IAS=150.0,TAS=171.3,Mach=0.512,AOA=-2.3,FuelVolume=1235", builder.ToString());
        Assert.Null(state.Get(TacviewTelemetryProperty.AGL));
    }

    [Fact]
    public void EncoderSkipsChangesBelowThresholdButCatchesSlowDrift()
    {
        var builder = new StringBuilder();
        TacviewTelemetry state = TacviewTelemetryEncoder.AppendChanges(
            builder, new TacviewTelemetry().Set(TacviewTelemetryProperty.TAS, 100.0), null, null);

        builder.Clear();
        state = TacviewTelemetryEncoder.AppendChanges(
            builder, new TacviewTelemetry().Set(TacviewTelemetryProperty.TAS, 100.6), state, null);
        Assert.Equal(string.Empty, builder.ToString());

        // Compared against the last written value, not the last sample.
        state = TacviewTelemetryEncoder.AppendChanges(
            builder, new TacviewTelemetry().Set(TacviewTelemetryProperty.TAS, 101.1), state, null);
        Assert.Equal(",TAS=101.1", builder.ToString());
        Assert.Equal(101.1, state.Get(TacviewTelemetryProperty.TAS));
    }

    [Fact]
    public void EncoderWritesLockedTargetOnceAndAgainAfterRelease()
    {
        var builder = new StringBuilder();
        TacviewTelemetry state = TacviewTelemetryEncoder.AppendChanges(
            builder, new TacviewTelemetry().SetLock(true, 0x105), null, _ => true);
        Assert.Equal(",LockedTarget=105,LockedTargetMode=1", builder.ToString());

        builder.Clear();
        state = TacviewTelemetryEncoder.AppendChanges(
            builder, new TacviewTelemetry().SetLock(true, 0x105), state, _ => true);
        Assert.Equal(string.Empty, builder.ToString());

        state = TacviewTelemetryEncoder.AppendChanges(
            builder, new TacviewTelemetry().SetLock(false, null), state, _ => true);
        Assert.Equal(",LockedTargetMode=0", builder.ToString());

        builder.Clear();
        TacviewTelemetryEncoder.AppendChanges(
            builder, new TacviewTelemetry().SetLock(true, 0x105), state, _ => true);
        Assert.Equal(",LockedTarget=105,LockedTargetMode=1", builder.ToString());
    }

    [Fact]
    public void EncoderDefersUnknownLockTargetUntilItExists()
    {
        var builder = new StringBuilder();
        TacviewTelemetry state = TacviewTelemetryEncoder.AppendChanges(
            builder, new TacviewTelemetry().SetLock(true, 0x200), null, _ => false);
        Assert.Equal(",LockedTargetMode=1", builder.ToString());

        builder.Clear();
        TacviewTelemetryEncoder.AppendChanges(
            builder, new TacviewTelemetry().SetLock(true, 0x200), state, _ => true);
        Assert.Equal(",LockedTarget=200", builder.ToString());
    }

    [Fact]
    public void MergerAttachesToUpsertsAddsTelemetryOnlyAndSkipsDeletes()
    {
        var updates = new List<TacviewEntityUpdate>
        {
            TacviewEntityUpdate.Upsert(0x101, Transform(1.0), Metadata("One")),
            TacviewEntityUpdate.Delete(0x102, TacviewEntityRemovalReason.Destroyed, "Two")
        };
        var telemetry = new List<KeyValuePair<ulong, TacviewTelemetry>>
        {
            Pair(0x101, 100.0),
            Pair(0x102, 100.0),
            Pair(0x103, 100.0),
            new KeyValuePair<ulong, TacviewTelemetry>(0x104, new TacviewTelemetry())
        };

        TacviewTelemetryMerger.Merge(updates, telemetry);

        Assert.Equal(3, updates.Count);
        Assert.NotNull(updates[0].Telemetry);
        Assert.False(updates[0].IsTelemetryOnly);
        Assert.Null(updates[1].Telemetry);
        Assert.True(updates[2].IsTelemetryOnly);
        Assert.Equal(0x103UL, updates[2].ObjectId);
    }

    [Fact]
    public void WriterEmitsTelemetryWithTransformAndAsTelemetryOnlyLines()
    {
        var text = new StringWriter();
        using (var writer = new TacviewEntityWriter(text, "Flight", ReferenceTime, leaveOpen: true))
        {
            writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.Upsert(0x101, Transform(1.0), Metadata("One"))
                    .WithTelemetry(new TacviewTelemetry().Set(TacviewTelemetryProperty.TAS, 200.0))
            }, new List<TacviewEvent>());

            // Unchanged transform, telemetry changed: telemetry-only line.
            writer.WriteFrame(0.5, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.TelemetryOnly(0x101, new TacviewTelemetry().Set(TacviewTelemetryProperty.TAS, 210.0))
            }, new List<TacviewEvent>());

            // Below threshold: nothing at all for this object.
            writer.WriteFrame(1.0, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.Upsert(0x101, Transform(1.0), Metadata("One"))
                    .WithTelemetry(new TacviewTelemetry().Set(TacviewTelemetryProperty.TAS, 210.4))
            }, new List<TacviewEvent>());

            // Telemetry for an object never written is dropped.
            writer.WriteFrame(1.5, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.TelemetryOnly(0x150, new TacviewTelemetry().Set(TacviewTelemetryProperty.TAS, 1.0))
            }, new List<TacviewEvent>());
        }

        string[] lines = text.ToString().Split('\n');
        Assert.Contains(lines, line => line.StartsWith("101,T=") && line.EndsWith(",TAS=200.0"));
        Assert.Contains("101,TAS=210.0", lines);
        Assert.DoesNotContain(lines, line => line.Contains("TAS=210.4"));
        Assert.DoesNotContain(lines, line => line.StartsWith("150"));
    }

    [Fact]
    public void WriterDoesNotDuplicateLockedTargetWrittenByMetadata()
    {
        var text = new StringWriter();
        using (var writer = new TacviewEntityWriter(text, "Lock", ReferenceTime, leaveOpen: true))
        {
            writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.Upsert(0x101, Transform(1.0), Metadata("Target")),
                TacviewEntityUpdate.Upsert(0x102, Transform(2.0), Metadata("Missile", 0x101))
                    .WithTelemetry(new TacviewTelemetry().SetLock(true, 0x101))
            }, new List<TacviewEvent>());
        }

        string missileLine = text.ToString().Split('\n').Single(line => line.StartsWith("102,"));
        Assert.Equal(1, CountOccurrences(missileLine, "LockedTarget="));
        Assert.Contains("LockedTargetMode=1", missileLine);
    }

    [Fact]
    public void RealtimeMirrorKeepsTelemetryForLateClients()
    {
        var text = new StringWriter();
        using (var writer = new TacviewEntityWriter(text, "Mirror", ReferenceTime, leaveOpen: true))
        {
            writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.Upsert(0x101, Transform(1.0), Metadata("One"))
                    .WithTelemetry(new TacviewTelemetry().Set(TacviewTelemetryProperty.Mach, 0.8))
            }, new List<TacviewEvent>());
        }

        var mirror = new TacviewRealtimeMirror();
        mirror.Apply(text.ToString());
        Assert.Contains("Mach=0.800", mirror.RenderSnapshot());
    }

    [Fact]
    public void TelemetryOnlyUpdateForDeletedObjectIsIgnored()
    {
        var text = new StringWriter();
        using (var writer = new TacviewEntityWriter(text, "Deleted", ReferenceTime, leaveOpen: true))
        {
            writer.WriteFrame(0.0, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.Upsert(0x101, Transform(1.0), Metadata("One"))
            }, new List<TacviewEvent>());
            writer.WriteFrame(0.5, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.Delete(0x101, TacviewEntityRemovalReason.Destroyed, "One")
            }, new List<TacviewEvent>());
            writer.WriteFrame(1.0, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.TelemetryOnly(0x101, new TacviewTelemetry().Set(TacviewTelemetryProperty.TAS, 1.0))
            }, new List<TacviewEvent>());
        }

        Assert.DoesNotContain("TAS=", text.ToString());
    }

    [Fact]
    public void RateGateEntersAtMostOncePerInterval()
    {
        var gate = new TelemetryRateGate(2.0);
        Assert.True(gate.TryEnter(10.0));
        Assert.False(gate.TryEnter(10.2));
        Assert.True(gate.TryEnter(10.5));
        Assert.False(gate.TryEnter(10.9));
        Assert.True(gate.TryEnter(20.0));
        Assert.False(gate.TryEnter(20.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryRateGate(0.0));
    }

    [Theory]
    [InlineData(0.0, 340.3, 1.225)]
    [InlineData(11000.0, 295.1, 0.3639)]
    public void StandardAtmosphereMatchesReferenceValues(double altitude, double speedOfSound, double density)
    {
        Assert.Equal(speedOfSound, FlightDataMath.SpeedOfSound(altitude), 1);
        Assert.Equal(density, FlightDataMath.AirDensity(altitude), 3);
    }

    [Fact]
    public void AirDataEstimatesAreConsistent()
    {
        Assert.Equal(1.0, FlightDataMath.Mach(340.294, 0.0), 3);
        Assert.Equal(200.0, FlightDataMath.IndicatedAirspeed(200.0, 0.0), 6);
        Assert.True(FlightDataMath.IndicatedAirspeed(200.0, 6000.0) < 200.0);
        Assert.Equal(45.0, FlightDataMath.AngleOfAttack(0.0, -1.0, 1.0), 6);
        Assert.Equal(-10.0, FlightDataMath.AngleOfAttack(0.0, Math.Tan(10.0 * Math.PI / 180.0), 1.0), 6);
        Assert.Equal(30.0, FlightDataMath.AngleOfSideslip(0.5, 0.0, Math.Sqrt(0.75)), 6);
        Assert.Equal(0.0, FlightDataMath.AngleOfSideslip(0.0, 0.0, 0.0));
    }

    [Fact]
    public void SettingsParseFlightDataKeysAndAliases()
    {
        var warnings = new List<string>();
        TacviewRealtimeSettings settings = TacviewRealtimeSettings.Parse(
            "export_flight_data=false\nExportEngagement=no\nexport_sensors=maybe\nflight_data_rate_hz=5\nflight_data_rate_hz=50\n",
            warnings);

        Assert.False(settings.ExportFlightData);
        Assert.False(settings.ExportEngagement);
        Assert.True(settings.ExportSensors);
        Assert.Equal(5.0, settings.FlightDataRateHz);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void DefaultSettingsFileEnablesFlightDataAtTwoHertz()
    {
        var warnings = new List<string>();
        TacviewRealtimeSettings settings = TacviewRealtimeSettings.Parse(TacviewRealtimeSettings.DefaultFileContent, warnings);

        Assert.Empty(warnings);
        Assert.True(settings.ExportFlightData);
        Assert.True(settings.ExportEngagement);
        Assert.True(settings.ExportSensors);
        Assert.Equal(2.0, settings.FlightDataRateHz);
    }

    private static KeyValuePair<ulong, TacviewTelemetry> Pair(ulong id, double tas)
    {
        return new KeyValuePair<ulong, TacviewTelemetry>(
            id,
            new TacviewTelemetry().Set(TacviewTelemetryProperty.TAS, tas));
    }

    private static EntityTransform Transform(double longitude)
    {
        return new EntityTransform(longitude, 2.0, 1000.0, 0.0, 0.0, 90.0, 10.0, 20.0, 90.0);
    }

    private static EntityMetadata Metadata(string name, ulong? lockedTarget = null)
    {
        return new EntityMetadata("Air+FixedWing", name, "Allies", "Blue", lockedTargetObjectId: lockedTarget);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
