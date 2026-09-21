using Newtonsoft.Json;
using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

#nullable disable

namespace CompostBin
{
    public class CompostBinConfig : CompostSettings
    {
        public const string FileName = "compostbin.json";
        public const string DurationKey = "compostbin:compostingDurationHours";
        public const double DefaultDurationHours = 480;

        public const string SettingsKey = "compostbin:settings";
        private const string EncodingPrefix = "base64:";

        public void Publish(ITreeAttribute worldConfig)
        {
            worldConfig.SetDouble(DurationKey, CompostingDurationHours);
            // StringAttribute.ToJsonToken does not escape quotes. Raw JSON here
            // breaks the world-edit screen when it parses the saved config tree.
            string json = JsonConvert.SerializeObject(this);
            worldConfig.SetString(SettingsKey, EncodingPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        }

        public static CompostBinConfig Read(string json)
        {
            try
            {
                // Continue accepting the raw JSON stored by older mod versions.
                if (json != null && json.StartsWith(EncodingPrefix, StringComparison.Ordinal))
                    json = Encoding.UTF8.GetString(Convert.FromBase64String(json.Substring(EncodingPrefix.Length)));
                var result = JsonConvert.DeserializeObject<CompostBinConfig>(json) ?? new CompostBinConfig();
                result.Validate();
                return result;
            }
            catch { return new CompostBinConfig(); }
        }

        public static bool ValidDuration(double value) => double.IsFinite(value) && value >= 1 && value <= 87600;

        public static double GetDuration(ITreeAttribute worldConfig)
        {
            double value = worldConfig?.GetDouble(DurationKey, DefaultDurationHours) ?? DefaultDurationHours;
            return ValidDuration(value) ? value : DefaultDurationHours;
        }

        public static CompostBinConfig Load(ICoreAPI api)
        {
            CompostBinConfig config;
            try
            {
                config = api.LoadModConfig<CompostBinConfig>(FileName);
            }
            catch (Exception e)
            {
                api.Logger?.Warning("[compostbin] Cannot read {0}; using default settings. File left unchanged. {1}", FileName, e.Message);
                return new CompostBinConfig();
            }
            config ??= new CompostBinConfig();
            var errors = config.Validate();
            foreach (string error in errors)
                api.Logger?.Warning("[compostbin] {0} File left unchanged.", error);
            if (errors.Count == 0)
            {
                // Expand older valid configs with the new settings, retaining user values.
                try
                {
                    api.StoreModConfig(config, FileName);
                }
                catch (Exception e)
                {
                    api.Logger?.Warning("[compostbin] Cannot write {0}; using loaded settings for this session. {1}", FileName, e.Message);
                }
            }
            return config;
        }
    }
}
