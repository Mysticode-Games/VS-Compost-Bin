using CompostBin;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;

static class ConfigChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool success, string message) { if (!success) throw new Exception(message); checks++; }
        foreach (string json in new[] { null, "{}", "{\"CompostingDurationHours\":240}",
            "{\"CompostingDurationHours\":0}", "{\"CompostingDurationHours\":-1}",
            "{\"CompostingDurationHours\":87601}", "{\"CompostingDurationHours\":\"NaN\"}",
            "{\"CompostingDurationHours\":\"Infinity\"}", "broken json" })
        {
            int writes = 0;
            var api = Stub.Make<ICoreAPI>((m, a) => {
                if (m.Name == "LoadModConfig") return json == null ? null : JsonConvert.DeserializeObject<CompostBinConfig>(json);
                if (m.Name == "StoreModConfig") writes++;
                return null;
            });
            var config = CompostBinConfig.Load(api);
            Check(config.CompostingDurationHours == (json?.Contains(":240") == true ? 240 : 480), "Config value or fallback: " + json);
            bool valid = json == null || json == "{}" || json.Contains(":240");
            Check(writes == (valid ? 1 : 0), "Expand valid configs; preserve invalid files: " + json);
        }
        Action loaded = null;
        var worldConfig = new TreeAttribute();
        worldConfig.SetString(CompostBinConfig.SettingsKey, "{\"CompostingDurationHours\":120}");
        var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name == "get_Config" ? worldConfig : null);
        var events = Stub.Make<IServerEventAPI>((m, a) => {
            if (m.Name == "add_SaveGameLoaded") loaded += (Action)a[0];
            if (m.Name == "remove_SaveGameLoaded") loaded -= (Action)a[0];
            return m.Name == "RegisterGameTickListener" ? 1L : null;
        });
        var server = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch {
            "get_World" => world, "get_Event" => events,
            "LoadModConfig" => new CompostBinConfig { CompostingDurationHours = 240 }, _ => null
        });
        var mod = new CompostBinModSystem();
        mod.StartServerSide(server);
        Check(loaded != null, "Server subscribes to world readiness");
        loaded();
        Newtonsoft.Json.Linq.JObject.Parse(worldConfig.ToJsonToken());
        Check(CompostBinConfig.Read(worldConfig.GetString(CompostBinConfig.SettingsKey)).CompostingDurationHours == 240,
            "Loading an affected save replaces legacy JSON with safe server settings");
        Check(CompostBinConfig.GetDuration(worldConfig) == 240, "Server publishes duration to world settings");
        var clientConfig = worldConfig.Clone();
        var clientWorld = Stub.Make<IWorldAccessor>((m, a) => m.Name == "get_Config" ? clientConfig : null);
        var client = Stub.Make<ICoreAPI>((m, a) => m.Name == "get_World" ? clientWorld : null);
        Check(new FireTestBin { Api = client }.CompostingDurationHours == 240, "Client barrel reads server-supplied duration");
        // A client can change its own presentation, but that tree is not a
        // writable reference to the server's world configuration.
        var serverBin = new FireTestBin { Api = server };
        var clientBin = new FireTestBin { Api = client };
        new CompostBinConfig { CompostingDurationHours = 1, RotPerCompost = 1, EnableSelfIgnition = false,
            DecompositionSpeedMultiplier = 20 }.Publish(clientConfig);
        Check(clientBin.CompostingDurationHours == 1, "Client edits affect only its local display copy");
        Check(serverBin.CompostingDurationHours == 240, "Client duration override does not change server duration");
        Check(serverBin.Settings.RotPerCompost == 4, "Client yield override does not change server yield");
        Check(serverBin.Settings.EnableSelfIgnition, "Client fire override does not change server ignition");
        Check(serverBin.Settings.DecompositionSpeedMultiplier == 1, "Client speed override does not change server rate");
        mod.Dispose();
        Check(loaded == null, "World readiness subscription removed on disposal");
        Console.WriteLine($"Passed {checks} config checks (JSON, defaults, validation, preservation, server settings, client reading).");
    }
}
