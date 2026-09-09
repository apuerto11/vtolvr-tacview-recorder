using System;

namespace VTOLVRTacviewSPRecorder.Core;

public enum GunAmmoPollObservation
{
    Baseline,
    NoShot,
    ShotObserved,
    Reloaded,
    UnconfirmedDecrease,
    Invalid
}

/// <summary>
/// Pure state guard for the VTOLCannon fallback.  It never infers a shot until
/// it has a baseline, and it fails closed for invalid state, reloads, a stopped
/// gun, or an implausibly large ammo jump.
/// </summary>
public sealed class GunAmmoPollState
{
    private const double MinimumRpm = 1.0;
    private const double MaximumRpm = 20000.0;
    private int _previousAmmo;
    private double _previousObservationSeconds;
    private bool _hasBaseline;

    public GunAmmoPollObservation Observe(
        int ammo,
        int maxAmmo,
        bool isFiring,
        double rpm,
        double nowSeconds)
    {
        ValidateFinite(nowSeconds, nameof(nowSeconds));
        if (maxAmmo < 1 || ammo < 0 || ammo > maxAmmo ||
            double.IsNaN(rpm) || double.IsInfinity(rpm) ||
            rpm < MinimumRpm || rpm > MaximumRpm)
        {
            _hasBaseline = false;
            return GunAmmoPollObservation.Invalid;
        }

        if (!_hasBaseline)
        {
            SetBaseline(ammo, nowSeconds);
            return GunAmmoPollObservation.Baseline;
        }

        double elapsedSeconds = nowSeconds - _previousObservationSeconds;
        if (elapsedSeconds < 0.0)
        {
            SetBaseline(ammo, nowSeconds);
            return GunAmmoPollObservation.Invalid;
        }

        int previousAmmo = _previousAmmo;
        SetBaseline(ammo, nowSeconds);
        if (ammo > previousAmmo)
        {
            return GunAmmoPollObservation.Reloaded;
        }

        if (ammo == previousAmmo)
        {
            return GunAmmoPollObservation.NoShot;
        }

        if (!isFiring)
        {
            return GunAmmoPollObservation.UnconfirmedDecrease;
        }

        int observedDecrease = previousAmmo - ammo;
        int maximumPlausibleDecrease = Math.Max(
            1,
            (int)Math.Ceiling((rpm / 60.0) * elapsedSeconds) + 1);
        return observedDecrease <= maximumPlausibleDecrease
            ? GunAmmoPollObservation.ShotObserved
            : GunAmmoPollObservation.UnconfirmedDecrease;
    }

    public void Reset()
    {
        _hasBaseline = false;
        _previousAmmo = 0;
        _previousObservationSeconds = 0.0;
    }

    private void SetBaseline(int ammo, double nowSeconds)
    {
        _previousAmmo = ammo;
        _previousObservationSeconds = nowSeconds;
        _hasBaseline = true;
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Gun ammo observation time must be finite.");
        }
    }
}
