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
    [BepInPlugin("com.henw.sulfurnogore", "Sulfur No Gore", "1.0.2")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // One player-facing switch per kind of effect. Each group lists the game methods it
        // skips ("Namespace.Type", "Method"); every overload of a method is skipped.
        // Config keys can't contain = \n \t \ " ' [ ], so keep the labels plain.
        private sealed class Group
        {
            public string Label;
            public string Description;
            public (string type, string method)[] Targets;
            public ConfigEntry<bool> Entry;
        }

        private static readonly Group[] Groups =
        {
            new Group
            {
                Label = "Blood splatters",
                Description = "Blood sprays and splashes when something is hit.",
                Targets = new[]
                {
                    ("PerfectRandom.Sulfur.Gameplay.BloodProjectileSpawner", "CreateEffect"),
                    ("PerfectRandom.Sulfur.Gameplay.BloodProjectileSpawner", "TriggerEffect"),
                    ("PerfectRandom.Sulfur.Core.Effects.BloodSplafsEffect", "StartBloodSplaf"),
                    ("PerfectRandom.Sulfur.Core.Effects.BloodProjectileSystemNEW", "Spawn"),
                    ("PerfectRandom.Sulfur.Core.Units.Unit", "CreateBloodProjectiles"),
                }
            },
            new Group
            {
                Label = "Blood stains",
                Description = "Blood left on the ground and walls.",
                Targets = new[]
                {
                    ("PerfectRandom.Sulfur.Core.Effects.BloodProjectileSystemNEW", "SpawnDecal"),
                }
            },
            new Group
            {
                Label = "Gibs and body chunks",
                Description = "Chunks flying off when enemies die or explode.",
                Targets = new[]
                {
                    ("PerfectRandom.Sulfur.Core.Effects.GibSystemNEW", "Spawn"),
                    ("PerfectRandom.Sulfur.Core.Effects.GibSystemNEW", "SpawnGibSet"),
                    ("PerfectRandom.Sulfur.Core.Effects.GibSystemNEW", "SpawnGibSetForUnit"),
                    ("PerfectRandom.Sulfur.Core.Units.Npc", "SpawnGibExplosion"),
                }
            },
            new Group
            {
                Label = "Hit effects and slash marks",
                Description = "The blood and slash marks shown when a unit takes damage. "
                    + "Turn this off if enemies freeze or you can't die.",
                Targets = new[]
                {
                    ("PerfectRandom.Sulfur.Core.Units.Unit", "CreateDamageEffects"),
                    ("PerfectRandom.Sulfur.Core.Units.Npc", "CreateDamageEffects"),
                }
            },
            new Group
            {
                Label = "Bullet holes",
                Description = "Bullet-hole marks on enemies.",
                Targets = new[]
                {
                    ("PerfectRandom.Sulfur.Core.Units.Npc", "DrawBulletHole"),
                }
            },
        };

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> BrownBlood;

        // Dirt-like brown. The (0.64, 0.11, 0.13) in dnSpy is only the field initializer; the
        // real colours come from the game's assets, so this is applied when the data is read.
        private static readonly UnityEngine.Color Brown = new UnityEngine.Color(0.40f, 0.27f, 0.16f);
        private static readonly Dictionary<PerfectRandom.Sulfur.Core.BloodEffectData, UnityEngine.Color> OriginalColors =
            new Dictionary<PerfectRandom.Sulfur.Core.BloodEffectData, UnityEngine.Color>();

        // Which group's switch controls each patched method, looked up live by the prefix.
        private static readonly Dictionary<MethodBase, ConfigEntry<bool>> Switches =
            new Dictionary<MethodBase, ConfigEntry<bool>>();

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enable mod", true, "Master switch for every effect below.");
            BrownBlood = Config.Bind("General", "Brown blood", true,
                "Tint any blood that is still shown brown, like dirt.");

            var harmony = new Harmony("com.henw.sulfurnogore");
            var prefix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(SkipOriginal),
                BindingFlags.Static | BindingFlags.NonPublic));

            int patched = 0;
            foreach (var group in Groups)
            {
                group.Entry = Config.Bind("Effects", group.Label, true, group.Description);

                foreach (var (typeName, methodName) in group.Targets)
                {
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
                            Switches[m] = group.Entry;
                            patched++;
                            Log.LogInfo($"Patched {typeName}.{methodName} ({group.Label})");
                        }
                        catch (Exception e)
                        {
                            Log.LogError($"Failed to patch {typeName}.{methodName}: {e}");
                        }
                    }
                }
            }

            // Brown blood: recolour the blood data each time a unit hands it out.
            var unitType = AccessTools.TypeByName("PerfectRandom.Sulfur.Core.Units.Unit");
            var bloodGetter = unitType != null
                ? AccessTools.PropertyGetter(unitType, "currentBloodEffectData") : null;
            if (bloodGetter != null)
            {
                harmony.Patch(bloodGetter, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(
                    nameof(TintBlood), BindingFlags.Static | BindingFlags.NonPublic)));
                patched++;
            }
            else
            {
                Log.LogWarning("Unit.currentBloodEffectData getter not found, brown blood disabled");
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
        // Checked on every call, so config/menu changes apply immediately.
        private static bool SkipOriginal(MethodBase __originalMethod)
        {
            bool skip = Enabled.Value
                && (!Switches.TryGetValue(__originalMethod, out var entry) || entry.Value);
            return !skip;
        }

        // Postfix on the getter: __result is the BloodEffectData the unit is about to use.
        private static void TintBlood(ref PerfectRandom.Sulfur.Core.BloodEffectData __result)
        {
            var data = __result;
            if (data == null) return;

            if (Enabled.Value && BrownBlood.Value)
            {
                if (!OriginalColors.ContainsKey(data))
                    OriginalColors[data] = data.bloodColor;
                data.bloodColor = Brown;
            }
            else if (OriginalColors.TryGetValue(data, out var original))
            {
                // Switched off: put the game's own colour back.
                data.bloodColor = original;
            }
        }

        private void OnDestroy()
        {
            SulfurOptionsApi.UnregisterPage("com.henw.sulfurnogore");
        }

        private void BuildPage(SulfurOptionsContext ctx)
        {
            ctx.AddSection("No Gore");
            ctx.AddDescription("Choose which kinds of gore to remove. Changes apply immediately.");

            AddToggle(ctx, Enabled, "Enable mod", "Master switch for everything below.");
            AddToggle(ctx, BrownBlood, "Brown blood", "Tint any blood that is still shown brown, like dirt.");

            ctx.AddSection("Effects to remove");
            foreach (var group in Groups)
                AddToggle(ctx, group.Entry, group.Label, group.Description);
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
