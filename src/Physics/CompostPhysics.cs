using System;

#nullable disable

namespace CompostBin
{
    // Consistent simulation units per game hour, not laboratory measurements.
    public static class CompostPhysics
    {
        private static readonly CompostSettings Defaults = new();
        public const double IgnitionTemperature = 120;
        public const double StepHours = 1.0 / 120;
        public static readonly double[] FaceConductance = { 1.0, 1.0, 1.0, 1.0, 1.1, 0.1 }; // N E S W up down
        public struct State
        {
            public double Temperature, DryMass, Water, Greens, Browns, Oxygen;
            // Dosage uses actual stack equivalents, independently of thermal mass.
            public double Peat, OrganicStacks, TurnAgeHours;
            public bool HasBeenTurned;
            public bool Sealed;
            public double Moisture => Water / Math.Max(0.001, DryMass + Water);
            public double Capacity => 2 + 1.5 * DryMass + 4.2 * Water;
        }
        public static double PeatDose(State s, CompostSettings settings = null)
        {
            settings ??= Defaults;
            return s.Sealed || s.Peat <= 0 || s.OrganicStacks <= 0 ? 0 :
                s.Peat / s.OrganicStacks / settings.PeatOptimalRatio;
        }
        public static double PeatDecompositionMultiplier(State s, CompostSettings settings = null)
        {
            settings ??= Defaults;
            return 1 + Math.Min(1, PeatDose(s, settings)) * settings.PeatDecompositionBonus;
        }
        public static double PeatAerationBenefit(State s, CompostSettings settings = null)
        {
            settings ??= Defaults;
            if (!s.HasBeenTurned || settings.PeatAerationDurationHours <= 0)
                return 0;
            return Math.Min(1, PeatDose(s, settings)) * settings.PeatAerationRetention
                * Math.Clamp(1 - s.TurnAgeHours / settings.PeatAerationDurationHours, 0, 1);
        }
        public static double Activity(State s, CompostSettings settings = null)
        {
            settings ??= Defaults;
            if (s.Sealed || s.Greens <= 0 || s.Temperature >= 75)
                return 0;
            double warmth = s.Temperature < 50 ? Math.Clamp((s.Temperature + 5) / 55, 0, 1)
                : s.Temperature <= 65 ? 1 : (75 - s.Temperature) / 10;
            double moisture = Math.Clamp(s.Moisture / 0.3, 0, 1) * Math.Clamp((0.85 - s.Moisture) / 0.2, 0, 1);
            double balance = s.Browns <= 0 ? 0.2 : Math.Clamp(s.Greens / s.Browns / settings.IdealGreenBrownRatioMin, 0, 1)
                * Math.Clamp(settings.IdealGreenBrownRatioMax * s.Browns / s.Greens, 0.2, 1);
            return warmth * moisture * balance * Math.Clamp(s.Oxygen / 0.45, 0, 1);
        }
        public static double Conductance(int face, CompostSettings settings) =>
            face == 4 ? settings.TopHeatConductance : face == 5 ? settings.BottomHeatConductance : settings.SideHeatConductance;
        public static double SharedConductance(int face, CompostSettings settings = null)
        {
            settings ??= Defaults;
            return settings.NeighborHeatExchangeMultiplier /
                (1 / Conductance(face, settings) + 1 / Conductance(Opposite(face), settings));
        }
        public static int Opposite(int face) => face < 4 ? (face + 2) % 4 : 9 - face;
        public static State Advance(State s, double ambient, double[] neighbors, double hours,
            out double burnedBrowns, out double biologicalHeat, CompostSettings settings = null)
        {
            settings ??= Defaults;
            double activity = Activity(s, settings);
            double peatDose = PeatDose(s, settings);
            biologicalHeat = 18 * settings.BiologicalHeatMultiplier * Math.Min(s.DryMass, s.Greens + s.Browns) * activity
                * (1 + Math.Min(1, peatDose) * settings.PeatBiologicalHeatBonus);
            double chemicalHeat = s.Sealed || s.Temperature < 60 ? 0 :
                s.Browns * 30 * settings.ChemicalHeatMultiplier * Math.Exp(Math.Min(8, (s.Temperature - 65) / 8))
                * Math.Clamp((0.55 - s.Moisture) / 0.35, 0, 1) * Math.Clamp(s.Oxygen, 0, 1)
                * (1 + Math.Max(0, peatDose - 1) * settings.PeatExcessHeatMultiplier);
            burnedBrowns = Math.Min(s.Browns, chemicalHeat * hours / 1800);
            chemicalHeat = hours > 0 ? burnedBrowns * 1800 / hours : 0;
            double loss = 0;
            for (int face = 0; face < 6; face++)
                loss += !settings.EnableNeighborHeatExchange || double.IsNaN(neighbors[face])
                    ? Conductance(face, settings) * (s.Temperature - ambient)
                    : SharedConductance(face, settings) * (s.Temperature - neighbors[face]);
            double evaporation = s.Sealed ? 0 : Math.Min(s.Water,
                Math.Max(0, s.Temperature - 25) * 0.00035 * settings.EvaporationMultiplier * s.DryMass * hours);
            double energy = (biologicalHeat + chemicalHeat - loss) * hours - evaporation * 45;
            s.Temperature = Math.Clamp(s.Temperature + energy / s.Capacity, -80, 500);
            s.Water = Math.Max(0, s.Water - evaporation);
            s.Browns -= burnedBrowns;
            s.DryMass = Math.Max(0, s.DryMass - burnedBrowns);
            double replenishment = s.Sealed ? 0 : 0.15 * settings.AerationRecoveryMultiplier * (1 - s.Oxygen) * Math.Clamp((0.85 - s.Moisture) / 0.3, 0, 1);
            s.Oxygen = Math.Clamp(s.Oxygen + (replenishment - 0.025 * activity * (1 - PeatAerationBenefit(s, settings))
                - chemicalHeat * 0.0004) * hours, 0, 1);
            if (s.HasBeenTurned)
                s.TurnAgeHours += hours;
            return s;
        }
        public static bool CanIgnite(State s, CompostSettings settings = null)
        {
            settings ??= Defaults;
            return settings.EnableSelfIgnition && CanStartSmoldering(s, settings);
        }
        public static bool CanStartSmoldering(State s, CompostSettings settings)
        {
            return !s.Sealed && s.Temperature >= settings.IgnitionTemperature
                && s.Browns >= 0.02 && s.Moisture < settings.IgnitionMaxMoisture && s.Oxygen >= settings.IgnitionMinAeration;
        }
    }
}
