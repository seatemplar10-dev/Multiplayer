using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld.BaseGen;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Static game state that <see cref="MultiplayerGame"/> resets when a session starts, so the host, every client and
    /// the arbiter start from the same values. The reset used to re-run the type initializers with
    /// <c>Type.TypeInitializer.Invoke(null, null)</c>. Mono runs a static constructor again that way; CoreCLR (RimWorld 1.7)
    /// returns without running it, which left every DebugSettings bool false after the "set all false" step (no damage,
    /// no storyteller, no random mental states or diseases, no anomaly darkening effecters) and never reset
    /// SymbolResolver_SingleThing.tmpRotations. Instead the values are captured once, when the mod is constructed and
    /// before anything sets them, and copied back on every reset.
    /// </summary>
    public static class StaticDefaults
    {
        private static Snapshot debugSettings;
        private static Snapshot singleThing;

        public static bool Captured => debugSettings != null;

        /// <summary>Called first thing in Multiplayer.InitMultiplayer (the mod's constructor). Runs once.</summary>
        public static void Capture(Action<string> message, Action<string> warning)
        {
            if (Captured)
                return;

            Snapshot settings, rotations;
            try
            {
                settings = new Snapshot(typeof(DebugSettings));
                rotations = new Snapshot(typeof(SymbolResolver_SingleThing));
            }
            catch (Exception e)
            {
                warning($"Multiplayer: capturing static defaults failed: {e}");
                return;
            }

            string ilCheck = settings.ApplyBoolInitializersFromIL(warning);
            foreach (var snapshot in new[] { settings, rotations })
                if (snapshot.skipped.Count > 0)
                    warning($"Multiplayer: static fields of {snapshot.type.Name} that cannot be restored: {string.Join(", ", snapshot.skipped)}");

            singleThing = rotations;
            debugSettings = settings;

            message($"Multiplayer: captured static defaults ({ilCheck}): {settings.Describe(live: false)}; {rotations.Describe(live: false)}");
        }

        /// <summary>Called by the MultiplayerGame constructor: copies the captured values back.</summary>
        public static void Restore(Action<string> message, Action<string> warning)
        {
            if (!Captured)
            {
                warning("Multiplayer: static defaults were not captured at mod construction, capturing now");
                Capture(message, warning);
                if (!Captured)
                    return;
            }

            debugSettings.Restore();
            singleThing.Restore();

            message($"Multiplayer: restored static defaults: {debugSettings.Describe(live: true)}; {singleThing.Describe(live: true)}");
        }

        private sealed class Snapshot
        {
            private const BindingFlags AllStatic =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

            public readonly Type type;
            public readonly List<FieldInfo> fields = new();
            public readonly List<object> values = new();
            public readonly List<string> skipped = new();

            public Snapshot(Type type)
            {
                this.type = type;

                foreach (var field in type.GetFields(AllStatic))
                {
                    if (field.IsLiteral)
                        continue;

                    object value = field.GetValue(null);

                    if (value is Array array)
                    {
                        // Copy the contents: the game shuffles these arrays in place
                        fields.Add(field);
                        values.Add(array.Clone());
                    }
                    else if (field.FieldType.IsValueType || value is string || value == null)
                    {
                        // A readonly scalar cannot change after the type initializer has run
                        if (field.IsInitOnly)
                            continue;

                        fields.Add(field);
                        values.Add(value);
                    }
                    else
                    {
                        skipped.Add(field.Name);
                    }
                }
            }

            public void Restore()
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];

                    if (values[i] is Array saved)
                    {
                        if (field.GetValue(null) is Array current && current.GetType() == saved.GetType() &&
                            current.Length == saved.Length)
                            Array.Copy(saved, current, saved.Length);
                        else if (!field.IsInitOnly)
                            field.SetValue(null, saved.Clone());
                    }
                    else
                    {
                        field.SetValue(null, values[i]);
                    }
                }
            }

            /// <summary>
            /// Cross-checks the captured bools against the constants the type initializer stores (C# emits
            /// ldc.i4.0/ldc.i4.1 + stsfld for them). Where they differ something changed the field before the capture:
            /// the initializer value wins, as it did on Mono.
            /// </summary>
            public string ApplyBoolInitializersFromIL(Action<string> warning)
            {
                Dictionary<string, bool> initial;
                try
                {
                    initial = BoolInitializersFromIL(type);
                }
                catch (Exception e)
                {
                    return $"initializer IL not readable: {e.GetType().Name}";
                }

                if (initial == null)
                    return "initializer IL not checked: not plain constant stores";

                var changed = new List<string>();
                for (int i = 0; i < fields.Count; i++)
                {
                    if (fields[i].FieldType != typeof(bool))
                        continue;

                    bool expected = initial.TryGetValue(fields[i].Name, out bool v) && v;
                    if ((bool)values[i] == expected)
                        continue;

                    changed.Add($"{fields[i].Name}={values[i]}->{expected}");
                    values[i] = expected;
                }

                if (changed.Count == 0)
                    return "initializer IL agrees";

                warning($"Multiplayer: {type.Name} was changed before the mod was constructed, using the initializer values: {string.Join(", ", changed)}");
                return $"initializer IL disagreed on {changed.Count}, initializer values used";
            }

            /// <param name="live">The fields' current values instead of the captured ones</param>
            public string Describe(bool live)
            {
                var parts = new List<string>();
                var trueBools = new List<string>();
                int bools = 0;

                for (int i = 0; i < fields.Count; i++)
                {
                    object value = live ? fields[i].GetValue(null) : values[i];

                    if (value is bool b)
                    {
                        bools++;
                        if (b) trueBools.Add(fields[i].Name);
                    }
                    else if (value is Array array)
                    {
                        parts.Add($"{fields[i].Name}=[{string.Join(", ", array.Cast<object>())}]");
                    }
                    else
                    {
                        parts.Add($"{fields[i].Name}={value}");
                    }
                }

                if (bools > 0)
                    parts.Insert(0, $"{trueBools.Count} of {bools} bools true [{string.Join(", ", trueBools)}]");

                return $"{type.Name} {string.Join("; ", parts)}";
            }
        }

        private static Dictionary<string, bool> BoolInitializersFromIL(Type type)
        {
            var result = new Dictionary<string, bool>();
            var cctor = type.TypeInitializer;
            if (cctor == null)
                return result;

            byte[] il = cctor.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
                return null;

            int pending = -1;
            for (int i = 0; i < il.Length;)
            {
                switch (il[i])
                {
                    case 0x00: // nop
                        i += 1;
                        break;
                    case 0x16: // ldc.i4.0
                    case 0x17: // ldc.i4.1
                        pending = il[i] - 0x16;
                        i += 1;
                        break;
                    case 0x80: // stsfld <field token>
                        if (pending < 0 || i + 5 > il.Length)
                            return null;
                        var field = type.Module.ResolveField(BitConverter.ToInt32(il, i + 1));
                        if (field == null || field.DeclaringType != type || field.FieldType != typeof(bool))
                            return null;
                        result[field.Name] = pending == 1;
                        pending = -1;
                        i += 5;
                        break;
                    case 0x2A: // ret
                        return pending < 0 ? result : null;
                    default:
                        return null;
                }
            }

            return null;
        }
    }
}
