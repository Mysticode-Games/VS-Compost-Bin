using System.Collections.Generic;
using System.Reflection;
using System;

#nullable disable

namespace CompostBin
{
    // Engine-independent settings, also used by the physics calibration harness.
    public class CompostSettings
    {
        [SettingRange(1, 87600)] public double CompostingDurationHours { get; set; } = 480;
        [SettingRange(1, 512)] public int MinimumRotToSeal { get; set; } = 64;
        [SettingRange(1, 64)] public int RotPerCompost { get; set; } = 4;
        [SettingRange(0, 20)] public double DecompositionSpeedMultiplier { get; set; } = 1;
        [SettingRange(0, 20)] public double BrownDecompositionSpeedMultiplier { get; set; } = 1;
        [SettingRange(0.0001, 10)] public double PeatOptimalRatio { get; set; } = 1d / 14;
        [SettingRange(0, 20)] public double PeatDecompositionBonus { get; set; } = 0.25;
        [SettingRange(0, 20)] public double PeatBiologicalHeatBonus { get; set; } = 0.25;
        [SettingRange(0, 87600)] public double PeatAerationDurationHours { get; set; } = 12;
        [SettingRange(0, 1)] public double PeatAerationRetention { get; set; } = 0.5;
        [SettingRange(0, 100)] public double PeatExcessHeatMultiplier { get; set; } = 3;
        [SettingRange(0.1, 87600)] public double PeatDecompositionHours { get; set; } = 6;
        [SettingRange(0, 1)] public double PeatRotYield { get; set; } = 0.0625;
        [SettingRange(0.1, 20)] public double IdealGreenBrownRatioMin { get; set; } = 1;
        [SettingRange(0.1, 20)] public double IdealGreenBrownRatioMax { get; set; } = 4;
        [SettingRange(0, 5)] public double BiologicalHeatMultiplier { get; set; } = 1;
        [SettingRange(0, 5)] public double ChemicalHeatMultiplier { get; set; } = 1;
        [SettingRange(0.01, 10)] public double TopHeatConductance { get; set; } = 1.1;
        [SettingRange(0.01, 10)] public double SideHeatConductance { get; set; } = 1;
        [SettingRange(0.01, 10)] public double BottomHeatConductance { get; set; } = 0.1;
        public bool EnableNeighborHeatExchange { get; set; } = true;
        [SettingRange(0, 5)] public double NeighborHeatExchangeMultiplier { get; set; } = 1;
        [SettingRange(0, 10)] public double EvaporationMultiplier { get; set; } = 1;
        [SettingRange(0, 10)] public double AerationRecoveryMultiplier { get; set; } = 1;
        public bool EnableSelfIgnition { get; set; } = true;
        [SettingRange(1, 4096)] public double SmolderConsumptionItemsPerHour { get; set; } = 64;
        [SettingRange(80, 400)] public double IgnitionTemperature { get; set; } = 120;
        [SettingRange(0.01, 0.8)] public double IgnitionMaxMoisture { get; set; } = 0.35;
        [SettingRange(0, 1)] public double IgnitionMinAeration { get; set; } = 0.1;
        [SettingRange(40, 120)] public double OverheatingTemperature { get; set; } = 70;
        [SettingRange(0, 1)] public double TurningCoolingFraction { get; set; } = 0.15;
        [SettingRange(0, 1)] public double TurningAeration { get; set; } = 1;
        [SettingRange(0, 1)] public double TurningHintAerationThreshold { get; set; } = 0.9;
        [SettingRange(0, 100)] public int TurningDurabilityCost { get; set; } = 1;
        [SettingRange(0, 32)] public double WateringAmount { get; set; } = 2;
        [SettingRange(0.1, 20)] public double WateringCanSeconds { get; set; } = 2;
        [SettingRange(0, 10)] public double EnvironmentalWaterMultiplier { get; set; } = 1;

        public List<string> Validate()
        {
            var errors = new List<string>();
            var defaults = new CompostSettings();
            foreach (var property in typeof(CompostSettings).GetProperties())
            {
                var range = property.GetCustomAttribute<SettingRangeAttribute>();
                if (range == null)
                    continue;
                double value = Convert.ToDouble(property.GetValue(this));
                if (double.IsFinite(value) && value >= range.Min && value <= range.Max)
                    continue;
                property.SetValue(this, property.GetValue(defaults));
                errors.Add($"{property.Name} must be between {range.Min} and {range.Max}; using its default.");
            }
            if (IdealGreenBrownRatioMin > IdealGreenBrownRatioMax)
            {
                IdealGreenBrownRatioMin = 1;
                IdealGreenBrownRatioMax = 4;
                errors.Add("Ideal green/brown ratio minimum exceeds maximum; using 1 and 4.");
            }
            if (OverheatingTemperature > IgnitionTemperature)
            {
                OverheatingTemperature = 70;
                errors.Add("OverheatingTemperature exceeds IgnitionTemperature; using 70.");
            }
            return errors;
        }
    }
}
