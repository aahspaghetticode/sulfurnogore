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
    [BepInPlugin("com.henw.sulfurnogore", "Sulfur No Gore", "1.0.3")]
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
        internal static ConfigEntry<bool> HideCorpses;

        // Pre-placed dead bodies ("Man Corpse") carry a BakedBulletHoles component; we find them
        // through it and switch the object off. Remembered so they can be switched back on.
        private static Type _bakedHolesType;
        private static readonly HashSet<UnityEngine.GameObject> HiddenCorpses =
            new HashSet<UnityEngine.GameObject>();

        // Blood projected onto level decorations (graveyards etc.): objects named "ProjectedBlood*".
        internal static ConfigEntry<bool> HideProjected;
        private static readonly HashSet<UnityEngine.GameObject> HiddenProjected =
            new HashSet<UnityEngine.GameObject>();

        // Brown blood: every Color field or material colour from #961b1e up to the game's blood
        // red, (0.64, 0.11, 0.13), and every shade in between, is replaced with a dirt-like
        // brown. The range is a box: each channel must sit between the two colours' values.
        // Alpha is kept.
        private static readonly UnityEngine.Color BloodRedLow = new UnityEngine.Color(150f / 255f, 27f / 255f, 30f / 255f);   // #961b1e
        private static readonly UnityEngine.Color BloodRedHigh = new UnityEngine.Color(0.64f, 0.11f, 0.13f);
        private static readonly UnityEngine.Color Brown = new UnityEngine.Color(0.40f, 0.27f, 0.16f);
        private const float ColorTolerance = 0.01f;

        // Undo actions for every colour changed, so turning the option off restores the originals.
        private static readonly List<Action> Restores = new List<Action>();
        // Objects already walked, so each one is only recoloured (and remembered) once.
        private static readonly HashSet<object> Scanned = new HashSet<object>();
        private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();

        // Which group's switch controls each patched method, looked up live by the prefix.
        private static readonly Dictionary<MethodBase, ConfigEntry<bool>> Switches =
            new Dictionary<MethodBase, ConfigEntry<bool>>();

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enable mod", true, "Master switch for every effect below.");
            HideProjected = Config.Bind("Effects", "Level blood decals", true,
                "Hides the blood decals placed around level decorations such as graveyards (ProjectedBlood).");
            HideCorpses = Config.Bind("Effects", "Dead bodies", true,
                "Hides the pre-placed dead bodies (Man Corpse) that decorate levels.");
            BrownBlood = Config.Bind("General", "Brown blood", true,
                "Any colour or material in the game's blood-red range is changed to dirt brown.");

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

            // Recolour now, again for every scene, and whenever either switch changes.
            ApplyBrownBlood();
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

            // Dead bodies: levels are generated while you play, so look for new ones every second.
            foreach (var t in AccessTools.AllTypes())
            {
                if (t.Name == "BakedBulletHoles") { _bakedHolesType = t; break; }
            }
            if (_bakedHolesType == null)
                Log.LogWarning("BakedBulletHoles type not found, dead bodies will not be hidden");
            InvokeRepeating(nameof(HideCorpsesTick), 1f, 1f);
            InvokeRepeating(nameof(HideProjectedTick), 1.5f, 2f);
            HideProjected.SettingChanged += (s, e) => RefreshProjected();
            Enabled.SettingChanged += (s, e) => RefreshProjected();
            HideCorpses.SettingChanged += (s, e) => RefreshCorpses();
            Enabled.SettingChanged += (s, e) => RefreshCorpses();
            BrownBlood.SettingChanged += (s, e) => ApplyBrownBlood();
            Enabled.SettingChanged += (s, e) => ApplyBrownBlood();

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
            if (__result == null || !Enabled.Value || !BrownBlood.Value) return;
            WalkColors(__result, 0);
        }

        // ---- Brown blood: find every blood-red colour and swap it ----

        private static bool IsBloodRed(UnityEngine.Color c)
        {
            return InRange(c.r, BloodRedLow.r, BloodRedHigh.r)
                && InRange(c.g, BloodRedLow.g, BloodRedHigh.g)
                && InRange(c.b, BloodRedLow.b, BloodRedHigh.b);
        }

        // Between the two values (either way round), with a little slack at both ends.
        private static bool InRange(float v, float a, float b)
        {
            float lo = Math.Min(a, b) - ColorTolerance;
            float hi = Math.Max(a, b) + ColorTolerance;
            return v >= lo && v <= hi;
        }

        private static UnityEngine.Color BrownWithAlpha(float alpha)
        {
            return new UnityEngine.Color(Brown.r, Brown.g, Brown.b, alpha);
        }

        private static bool IsGameType(Type t)
        {
            return t != null && t.IsClass && !typeof(UnityEngine.Object).IsAssignableFrom(t)
                && t.Namespace != null && t.Namespace.StartsWith("PerfectRandom");
        }

        private static bool ShouldDescend(Type t)
        {
            if (IsGameType(t)) return true;
            if (t.IsArray) return IsGameType(t.GetElementType());
            return t.IsGenericType && t.GetGenericArguments().Length == 1
                && IsGameType(t.GetGenericArguments()[0]);
        }

        // Instance fields of the type and of its game base classes (not Unity's).
        private static FieldInfo[] FieldsOf(Type type)
        {
            if (FieldCache.TryGetValue(type, out var cached)) return cached;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) break;
                list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            }
            var arr = list.ToArray();
            FieldCache[type] = arr;
            return arr;
        }

        // Recolours blood-red Color fields on a game object and on the game classes nested in it.
        private static void WalkColors(object obj, int depth)
        {
            if (obj == null || depth > 4 || !Scanned.Add(obj)) return;

            foreach (var f in FieldsOf(obj.GetType()))
            {
                try
                {
                    if (f.FieldType == typeof(UnityEngine.Color))
                    {
                        var c = (UnityEngine.Color)f.GetValue(obj);
                        if (!IsBloodRed(c)) continue;

                        var target = obj;
                        var field = f;
                        field.SetValue(target, BrownWithAlpha(c.a));
                        Restores.Add(() =>
                        {
                            if (target is UnityEngine.Object u && u == null) return;
                            field.SetValue(target, c);
                        });
                    }
                    else if (ShouldDescend(f.FieldType))
                    {
                        var v = f.GetValue(obj);
                        if (v is System.Collections.IEnumerable items)
                        {
                            foreach (var item in items) WalkColors(item, depth + 1);
                        }
                        else
                        {
                            WalkColors(v, depth + 1);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogDebug($"Colour scan skipped {obj.GetType().Name}.{f.Name}: {e.Message}");
                }
            }
        }

        private static void RecolorMaterial(UnityEngine.Material m)
        {
            if (m == null || m.shader == null) return;
            var shader = m.shader;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Color) continue;
                string name = shader.GetPropertyName(i);
                var c = m.GetColor(name);
                if (!IsBloodRed(c)) continue;

                m.SetColor(name, BrownWithAlpha(c.a));
                var mat = m;
                Restores.Add(() => { if (mat != null) mat.SetColor(name, c); });
            }
        }

        private static void ScanAll()
        {
            foreach (var so in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.ScriptableObject>())
                if (so != null && IsGameNamespace(so.GetType())) WalkColors(so, 0);

            foreach (var mb in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.MonoBehaviour>())
                if (mb != null && IsGameNamespace(mb.GetType())) WalkColors(mb, 0);

            foreach (var m in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Material>())
            {
                try { RecolorMaterial(m); }
                catch (Exception e) { Log.LogDebug($"Material scan skipped: {e.Message}"); }
            }

            Log.LogInfo($"Brown blood: {Restores.Count} colours recoloured.");
        }

        private static bool IsGameNamespace(Type t)
        {
            return t.Namespace != null && t.Namespace.StartsWith("PerfectRandom");
        }

        // Puts every original colour back, then recolours again if the option is on.
        private static void ApplyBrownBlood()
        {
            foreach (var undo in Restores)
            {
                try { undo(); } catch { }
            }
            Restores.Clear();
            Scanned.Clear();

            if (Enabled.Value && BrownBlood.Value) ScanAll();
        }

        // ---- Dead bodies ----

        private void HideCorpsesTick()
        {
            HideCorpsesNow();
        }

        private static void RefreshCorpses()
        {
            if (Enabled.Value && HideCorpses.Value) HideCorpsesNow();
            else RestoreCorpses();
        }

        private static void HideCorpsesNow()
        {
            HiddenCorpses.RemoveWhere(g => g == null);
            if (!Enabled.Value || !HideCorpses.Value || _bakedHolesType == null) return;

            // Only active objects are returned, so bodies already switched off are not revisited.
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_bakedHolesType))
            {
                var c = o as UnityEngine.Component;
                if (c == null) continue;

                var go = c.gameObject;
                if (go.name.StartsWith("Man Corpse") && HiddenCorpses.Add(go))
                    go.SetActive(false);
            }
        }

        // ---- Level blood decals ("ProjectedBlood*") ----
        // Everything else in the same folder (skeletons, graves, ...) is left alone.

        private void HideProjectedTick()
        {
            HideProjectedNow();
        }

        private static void RefreshProjected()
        {
            if (Enabled.Value && HideProjected.Value) HideProjectedNow();
            else RestoreProjected();
        }

        private static void HideProjectedNow()
        {
            HiddenProjected.RemoveWhere(g => g == null);
            if (!Enabled.Value || !HideProjected.Value) return;

            // Only active objects are returned, so ones already switched off are not revisited.
            foreach (var t in UnityEngine.Object.FindObjectsOfType<UnityEngine.Transform>())
            {
                if (t == null) continue;
                var go = t.gameObject;
                if (go.name.StartsWith("ProjectedBlood") && HiddenProjected.Add(go))
                    go.SetActive(false);
            }
        }

        private static void RestoreProjected()
        {
            foreach (var go in HiddenProjected)
            {
                if (go != null) go.SetActive(true);
            }
            HiddenProjected.Clear();
        }

        private static void RestoreCorpses()
        {
            foreach (var go in HiddenCorpses)
            {
                if (go != null) go.SetActive(true);
            }
            HiddenCorpses.Clear();
        }

        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene,
            UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            // New scenes bring new assets; already-recoloured ones no longer match, so just scan.
            if (Enabled.Value && BrownBlood.Value) ScanAll();
        }

        private void OnDestroy()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            SulfurOptionsApi.UnregisterPage("com.henw.sulfurnogore");
        }

        private void BuildPage(SulfurOptionsContext ctx)
        {
            ctx.AddSection("No Gore");
            ctx.AddDescription("Choose which kinds of gore to remove. Changes apply immediately.");

            AddToggle(ctx, Enabled, "Enable mod", "Master switch for everything below.");
            AddToggle(ctx, BrownBlood, "Brown blood",
                "Any colour or material in the game's blood-red range is changed to dirt brown.");

            ctx.AddSection("Effects to remove");
            foreach (var group in Groups)
                AddToggle(ctx, group.Entry, group.Label, group.Description);
            AddToggle(ctx, HideProjected, "Level blood decals",
                "Hides the blood decals placed around level decorations such as graveyards (ProjectedBlood).");
            AddToggle(ctx, HideCorpses, "Dead bodies",
                "Hides the pre-placed dead bodies (Man Corpse) that decorate levels.");
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
