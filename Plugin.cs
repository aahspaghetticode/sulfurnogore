using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Ryuka.Sulfur.NativeUI;

namespace SulfurNoGore
{
    [BepInDependency("ryuka.sulfur.nativeui", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin("com.henw.sulfurnogore", "Sulfur No Gore", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // "Namespace.Type", "Method" -> every overload of that method gets skipped.
        // Names come from the dnSpy/ILSpy screenshots.
        private static readonly (string type, string method)[] Targets =
        {
            // PerfectRandom.Sulfur.Gameplay.dll
            ("PerfectRandom.Sulfur.Gameplay.BloodProjectileSpawner", "CreateEffect"),
            ("PerfectRandom.Sulfur.Gameplay.BloodProjectileSpawner", "TriggerEffect"),

            // PerfectRandom.Sulfur.Core.dll (namespace ...Core.Effects)
            ("PerfectRandom.Sulfur.Core.Effects.BloodSplafsEffect", "StartBloodSplaf"),
            ("PerfectRandom.Sulfur.Core.Effects.BloodProjectileSystemNEW", "Spawn"),
            ("PerfectRandom.Sulfur.Core.Effects.BloodProjectileSystemNEW", "SpawnDecal"),
            ("PerfectRandom.Sulfur.Core.Effects.GibSystemNEW", "Spawn"),
            ("PerfectRandom.Sulfur.Core.Effects.GibSystemNEW", "SpawnGibSet"),
            ("PerfectRandom.Sulfur.Core.Effects.GibSystemNEW", "SpawnGibSetForUnit"),

            // Units: the callers that spawn hit effects, melee slash marks, bullet holes.
            // Harmony patches only the body of the type you name, not overrides in other
            // classes, so each override has to be listed separately.
            ("PerfectRandom.Sulfur.Core.Units.Unit", "CreateDamageEffects"),
            ("PerfectRandom.Sulfur.Core.Units.Unit", "CreateBloodProjectiles"),
            ("PerfectRandom.Sulfur.Core.Units.Npc", "CreateDamageEffects"),
            ("PerfectRandom.Sulfur.Core.Units.Npc", "DrawBulletHole"),
            ("PerfectRandom.Sulfur.Core.Units.Npc", "SpawnGibExplosion"),
        };

        // Optional per-target toggles could go here later.
        internal static ConfigEntry<bool> Enabled;
        // One switch per patched method, looked up live by the prefix.
        internal static readonly Dictionary<MethodBase, ConfigEntry<bool>> Switches =
            new Dictionary<MethodBase, ConfigEntry<bool>>();
        internal static readonly List<ConfigEntry<bool>> TargetEntries = new List<ConfigEntry<bool>>();

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Disable blood and gore effects.");
            var harmony = new Harmony("com.henw.sulfurnogore");
            var prefix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(SkipOriginal),
                BindingFlags.Static | BindingFlags.NonPublic));

            int patched = 0;
            foreach (var (typeName, methodName) in Targets)
            {
                // One switch per target so a broken patch can be found by bisecting.
                var cfgKey = typeName.Substring(typeName.LastIndexOf('.') + 1) + "." + methodName;
                var entry = Config.Bind("Targets", cfgKey, true, $"Skip {typeName}.{methodName}");
                TargetEntries.Add(entry);

                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Log.LogWarning($"Type not found: {typeName}");
                    continue;
                }

                var methods = new List<MethodBase>();
                foreach (var m in type.GetMethods(AccessTools.all))
                {
                    if (m.Name == methodName && m.DeclaringType == type && !m.IsAbstract)
                        methods.Add(m);
                }

                if (methods.Count == 0)
                {
                    Log.LogWarning($"Method not found: {typeName}.{methodName}");
                    continue;
                }

                foreach (var m in methods)
                {
                    try
                    {
                        harmony.Patch(m, prefix: prefix);
                        Switches[m] = entry;
                        patched++;
                        Log.LogInfo($"Patched {typeName}.{methodName}");
                    }
                    catch (Exception e)
                    {
                        Log.LogError($"Failed to patch {typeName}.{methodName}: {e}");
                    }
                }
            }

            // Not patched on purpose: the player's hurt flash (FullscreenEffects.hurtEffect).
            // Setting that field to null throws in Player.ManualUpdate every frame.

            Log.LogInfo($"Sulfur No Gore loaded, {patched} methods patched.");

            SulfurOptionsApi.RegisterPage(new SulfurOptionsPage
            {
                PageId = "com.henw.sulfurnogore",
                DisplayName = "No Gore",
                SortOrder = 1000,
                GetDisplayName = () => "No Gore",
                BuildPage = BuildPage
            });
        }

        // Returning false from a Harmony prefix skips the original method.
        // For non-void methods the caller gets default(T).
        // Checked on every call, so config/menu changes apply immediately.
        private static bool SkipOriginal(MethodBase __originalMethod)
        {
            bool skip = Enabled.Value
                && (!Switches.TryGetValue(__originalMethod, out var entry) || entry.Value);
            return !skip;
        }

        private void OnDestroy()
        {
            SulfurOptionsApi.UnregisterPage("com.henw.sulfurnogore");
        }

        private void BuildPage(SulfurOptionsContext ctx)
        {
            ctx.AddSection("No Gore");
            ctx.AddDescription("Turn individual patches off to find one that breaks gameplay.");

            AddToggle(ctx, Enabled, "Enabled", "Master switch for every patch below.");

            ctx.AddSection("Patched methods");
            foreach (var entry in TargetEntries)
                AddToggle(ctx, entry, entry.Definition.Key, entry.Description.Description);
        }

        // Call form from the library docs: AddSettingToggle(row, value, onChanged).
        // The parameter types are inferred (SulfurSettingRow, bool, Action<bool>).
        private static void AddToggle(SulfurOptionsContext ctx, ConfigEntry<bool> entry,
            string label, string description)
        {
            var row = new SulfurSettingRow { Label = label, Description = description };
            ctx.AddSettingToggle(row, entry.Value, v => entry.Value = v);
        }
    }
}
