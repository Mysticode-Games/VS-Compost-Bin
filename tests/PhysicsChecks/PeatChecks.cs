using CompostBin;

static class PeatChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        void Near(double actual, double expected, string message) =>
            Check(Math.Abs(actual - expected) < 1e-9, $"{message}: {actual} != {expected}");
        var settings = new CompostSettings();
        var air = Enumerable.Repeat(double.NaN, 6).ToArray();
        var half = new CompostPhysics.State {
            Temperature = 50, DryMass = 7.25, Greens = 5, Browns = 2,
            OrganicStacks = 7, Peat = .5, Water = 5, Oxygen = .8,
            HasBeenTurned = true
        };
        Near(CompostPhysics.PeatDose(half), 1, "Half peat stack with seven organic stacks is optimal");
        Near(CompostPhysics.PeatDecompositionMultiplier(half), 1.25, "Optimal default decomposition bonus");
        var full = half; full.Peat = 1;
        Near(CompostPhysics.PeatDose(full), 2, "Full stack is twice optimal dose");
        Near(CompostPhysics.PeatDecompositionMultiplier(full), 1.25, "Excess peat does not increase speed bonus");
        var small = half; small.Peat /= 4; small.OrganicStacks /= 4;
        Near(CompostPhysics.PeatDose(small), 1, "Dosage scales to smaller batches");
        var none = half; none.Peat = 0;
        Near(CompostPhysics.PeatDecompositionMultiplier(none), 1, "No peat leaves decomposition unchanged");
        CompostPhysics.Advance(none, 20, air, .01, out _, out double originalHeat);
        var retained = CompostPhysics.Advance(half, 20, air, .01, out _, out double boostedHeat);
        Near(boostedHeat, originalHeat * 1.25, "Peat adds biological heat at optimal dose");
        Near(CompostPhysics.PeatAerationBenefit(half), .5, "Fresh turning starts retention bonus");
        var expired = half; expired.TurnAgeHours = 12;
        Near(CompostPhysics.PeatAerationBenefit(expired), 0, "Retention expires");
        var unturned = half; unturned.HasBeenTurned = false;
        Near(CompostPhysics.PeatAerationBenefit(unturned), 0, "Fresh bins do not receive turning benefit");
        var unretained = CompostPhysics.Advance(unturned, 20, air, .01, out _, out _);
        Check(retained.Oxygen > unretained.Oxygen, "Turning retains more oxygen during active decomposition");
        Near(retained.TurnAgeHours, .01, "Physics advances turning age");
        var spent = retained; spent.Peat = 0;
        Near(CompostPhysics.PeatDecompositionMultiplier(spent), 1, "Spent peat immediately loses speed benefit");
        Near(CompostPhysics.PeatAerationBenefit(spent), 0, "Spent peat immediately loses aeration benefit");
        var disabled = new CompostSettings {
            PeatDecompositionBonus = 0, PeatBiologicalHeatBonus = 0,
            PeatAerationRetention = 0, PeatExcessHeatMultiplier = 0
        };
        var disabledStep = CompostPhysics.Advance(half, 20, air, .01, out _, out _, disabled);
        var ordinaryStep = CompostPhysics.Advance(none, 20, air, .01, out _, out _);
        Near(disabledStep.Temperature, ordinaryStep.Temperature, "Configuration can disable all peat heating bonuses");
        Near(disabledStep.Oxygen, ordinaryStep.Oxygen, "Configuration can disable peat oxygen retention");
        var wet = full; wet.Temperature = 65; wet.Water = 20;
        CompostPhysics.Advance(wet, 20, air, .01, out double wetBurn, out _);
        Near(wetBurn, 0, "Wet material blocks excess chemical heating");
        var dry = half; dry.Temperature = 65; dry.Water = 1.75;
        CompostPhysics.Advance(dry, 20, air, .01, out double halfBurn, out _);
        dry.Peat = 1;
        CompostPhysics.Advance(dry, 20, air, .01, out double fullBurn, out _);
        Near(fullBurn, halfBurn * 4, "Full stack quadruples dry chemical heating at default settings");
        var empty = full; empty.OrganicStacks = 0; empty.Greens = empty.Browns = 0;
        Near(CompostPhysics.PeatDose(empty), 0, "Peat without organics provides no boost");
        CompostPhysics.Advance(empty, 20, air, .01, out double emptyBurn, out double emptyHeat);
        Near(emptyBurn + emptyHeat, 0, "Peat alone supplies no free heat");
        var sealedPile = full; sealedPile.Sealed = true;
        Near(CompostPhysics.PeatDecompositionMultiplier(sealedPile), 1, "Sealed pile gets no peat boost");
        Near(CompostPhysics.PeatAerationBenefit(sealedPile), 0, "Sealed pile gets no peat aeration benefit");
        settings.PeatOptimalRatio = 1d / 7;
        Near(CompostPhysics.PeatDose(full, settings), 1, "Configured ratio moves optimal dosage to full stack");
        settings.PeatAerationDurationHours = 0;
        Near(CompostPhysics.PeatAerationBenefit(full, settings), 0, "Zero duration disables retention safely");
        settings.PeatOptimalRatio = double.NaN; settings.PeatRotYield = 2;
        Check(settings.Validate().Count == 2, "Invalid peat settings are rejected");
        Near(settings.PeatOptimalRatio, 1d / 14, "Invalid ratio restores default");

        // Warm, neglected barrel: fixed green stock isolates thermal response.
        // Approximate peat's transition clock; engine integration is checked separately.
        (bool ignited, double peak, double hours) Simulate(double peat, bool ignition)
        {
            var config = new CompostSettings { EnableSelfIgnition = ignition };
            var state = half; state.Peat = peat; state.DryMass = 7 + peat / 2;
            state.Water = 1.75; state.Temperature = 65; state.Oxygen = 1;
            double peak = state.Temperature, progress = 0;
            for (int i = 0; i < 24 * 120; i++)
            {
                progress += Math.Max(1, 3 * CompostPhysics.Activity(state, config))
                    * CompostPhysics.PeatDecompositionMultiplier(state, config) * CompostPhysics.StepHours;
                if (progress >= config.PeatDecompositionHours) state.Peat = 0;
                state = CompostPhysics.Advance(state, 20, air, CompostPhysics.StepHours, out _, out _, config);
                state.OrganicStacks = state.Greens + state.Browns;
                peak = Math.Max(peak, state.Temperature);
                if (CompostPhysics.CanStartSmoldering(state, config))
                {
                    Check(CompostPhysics.CanIgnite(state, config) == ignition, "Self ignition setting governs peat hazard");
                    return (true, peak, (i + 1) * CompostPhysics.StepHours);
                }
            }
            return (false, peak, 24);
        }
        var optimal = Simulate(.5, true);
        var excess = Simulate(1, true);
        var smolder = Simulate(1, false);
        Check(!optimal.ignited && excess.ignited && smolder.ignited,
            "Warm dry calibration: half stack cools, full stack reaches fire or smolder threshold");
        Check(excess.hours < 6, "Excess dose reaches hazard before its nominal decomposition period");
        Console.WriteLine($"Passed {checks} peat physics checks. Warm dry calibration peaks: half {optimal.peak:F1} C, full {excess.peak:F1} C; full-dose hazard after {excess.hours:F2} game hours.");
    }
}
