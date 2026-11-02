using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Patches
{
    // FIXES row 197, part 2: mouse presses and releases keep their real order and position on slow frames.
    // Waylith's ImguiDispatch.DispatchInput runs every press of a frame first, then every release, all at the
    // end-of-frame mouse position. On a slow frame (co-op) a fast shift-click series loses clicks: press 2 overwrites
    // the selector's drag box start, release 1 then selects at click 2's spot. Unity replays events in OS order.
    // This prefix replays the frame's mouse events in order, each at its own position, with its own click count and
    // modifiers, but ONLY in a frame where the order matters (a button has more than one event, other than one press
    // followed by one release). Every other frame runs the original dispatch untouched. The drag, keyboard and scroll
    // passes run exactly as in the original. Local UI input only: no sim state, nothing synced or saved.
    // Any setup or pre-dispatch failure falls back to the original method; the feature then turns itself off.
    // Known differences from the original in a replayed frame: every click now reaches its handler (two clicks on a
    // toggle gizmo toggle twice, as in Unity), and drag passes of other held buttons run after the replayed events.
    [HarmonyPatch]
    static class ImguiMouseReplay
    {
        static Type dispatchType, inputStateType, inputEventType;
        static FieldInfo backendField, lastPhysField, hasLastPhysField, mouseDeltaField;
        static FieldInfo evRawType, evButton, evPos, evClickCount, evShift, evControl, evAlt, evCommand;
        static MethodInfo readPhysMouse, readModifiers, runPass, runInputPass, dispatch, dispatchKeyboard, runScrollPass;
        static MethodInfo beginInputPhase, endInputPhase;
        static PropertyInfo eventsThisFrame, stateMousePosition, displayHeight;
        static object mouseDownValue, mouseUpValue;

        static bool disabled;
        static int logged;

        // Unity-space mouse position (origin bottom-left) for UI.MousePositionOnUI during a replayed pass
        internal static Vector2? overrideMouse;

        struct MouseEvt
        {
            public bool down;
            public int button;
            public Vector2 phys; // top-down framebuffer position, the space DispatchInput passes to Dispatch
            public int clickCount;
            public EventModifiers mods;
        }

        static readonly List<MouseEvt> events = new();
        static readonly int[] perButton = new int[3];

        static bool Prepare()
        {
            try
            {
                dispatchType = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImguiDispatch");
                inputStateType = AccessTools.TypeByName("Waylith.Input.InputState");
                inputEventType = AccessTools.TypeByName("Waylith.Input.InputEvent");
                var eventTypeEnum = AccessTools.TypeByName("Waylith.Input.InputEventType");
                if (dispatchType == null || inputStateType == null || inputEventType == null || eventTypeEnum == null)
                    return false;

                backendField = AccessTools.Field(dispatchType, "backend");
                lastPhysField = AccessTools.Field(dispatchType, "_lastPhysMouse");
                hasLastPhysField = AccessTools.Field(dispatchType, "_hasLastPhysMouse");
                mouseDeltaField = AccessTools.Field(dispatchType, "_mouseDelta");
                readPhysMouse = AccessTools.Method(dispatchType, "ReadPhysMouse");
                readModifiers = AccessTools.Method(dispatchType, "ReadModifiers");
                runPass = AccessTools.Method(dispatchType, "RunPass");
                runInputPass = AccessTools.Method(dispatchType, "RunInputPass");
                dispatch = AccessTools.Method(dispatchType, "Dispatch");
                dispatchKeyboard = AccessTools.Method(dispatchType, "DispatchKeyboard");
                runScrollPass = AccessTools.Method(dispatchType, "RunScrollPass");
                beginInputPhase = backendField == null ? null : AccessTools.Method(backendField.FieldType, "BeginInputPhase");
                endInputPhase = backendField == null ? null : AccessTools.Method(backendField.FieldType, "EndInputPhase");
                eventsThisFrame = AccessTools.Property(inputStateType, "EventsThisFrame");
                stateMousePosition = AccessTools.Property(inputStateType, "MousePosition");

                evRawType = AccessTools.Field(inputEventType, "rawType");
                evButton = AccessTools.Field(inputEventType, "mouseButton");
                evPos = AccessTools.Field(inputEventType, "MousePosition");
                evClickCount = AccessTools.Field(inputEventType, "clickCount");
                evShift = AccessTools.Field(inputEventType, "shift");
                evControl = AccessTools.Field(inputEventType, "control");
                evAlt = AccessTools.Field(inputEventType, "alt");
                evCommand = AccessTools.Field(inputEventType, "command");
                mouseDownValue = Enum.Parse(eventTypeEnum, "MouseDown");
                mouseUpValue = Enum.Parse(eventTypeEnum, "MouseUp");

                var hostParam = AccessTools.Method(dispatchType, "DispatchInput")?.GetParameters();
                if (hostParam is not { Length: 1 }) return false;
                displayHeight = AccessTools.Property(hostParam[0].ParameterType, "DisplayHeight");

                bool ok = backendField != null && lastPhysField != null && hasLastPhysField != null && mouseDeltaField != null
                    && readPhysMouse != null && readModifiers != null && runPass != null && runInputPass != null
                    && dispatch != null && dispatchKeyboard != null && runScrollPass != null
                    && beginInputPhase != null && endInputPhase != null && eventsThisFrame != null && displayHeight != null
                    && stateMousePosition?.PropertyType == typeof(System.Numerics.Vector2)
                    && evRawType != null && evButton != null && evPos != null && evClickCount != null
                    && evShift != null && evControl != null && evAlt != null && evCommand != null
                    && evPos.FieldType == typeof(System.Numerics.Vector2)
                    && runPass.GetParameters().Length == 6 && dispatch.GetParameters().Length == 2;
                if (!ok)
                    Log.Warning("Multiplayer: Waylith input dispatch differs from the expected shape; mouse event replay (row 197) is off");
                return ok;
            }
            catch (Exception e)
            {
                Log.Warning($"Multiplayer: mouse event replay (row 197) is off: {e.Message}");
                return false;
            }
        }

        static MethodBase TargetMethod() => AccessTools.Method(dispatchType, "DispatchInput");

        static bool Prefix(object __instance, object __0)
        {
            if (disabled) return true;

            // Cheap gate: a frame whose order matters always has a button both pressed and released in it
            if (!(Input.GetMouseButtonDown(0) && Input.GetMouseButtonUp(0))
                && !(Input.GetMouseButtonDown(1) && Input.GetMouseButtonUp(1))
                && !(Input.GetMouseButtonDown(2) && Input.GetMouseButtonUp(2)))
                return true;

            object backend;
            try
            {
                if (!CollectAndDecide()) return true;
                backend = backendField.GetValue(__instance);
            }
            catch (Exception e)
            {
                disabled = true;
                Log.Warning($"Multiplayer: mouse event replay (row 197) turned off, original input dispatch runs: {e}");
                return true;
            }

            beginInputPhase.Invoke(backend, null);
            try
            {
                Replay(__instance, __0);
            }
            catch (Exception e)
            {
                // Same handling as the original method: log and finish the frame. Then stay off: a repeating fault
                // would otherwise drop keyboard and scroll on every multi-click frame.
                disabled = true;
                Console.Error.WriteLine($"[ImguiDispatch] input dispatch threw (Multiplayer row 197 replay, now off): {e}");
                Log.Warning("Multiplayer: mouse event replay (row 197) failed and is now off for this session");
            }
            finally
            {
                overrideMouse = null;
                endInputPhase.Invoke(backend, null);
            }
            return false;
        }

        // Fills `events` with this frame's mouse press/release events in order. True when their order matters.
        static bool CollectAndDecide()
        {
            events.Clear();
            var list = (IList)eventsThisFrame.GetValue(null);
            if (list == null || list.Count < 2) return false;

            Array.Clear(perButton);
            foreach (var evt in list)
            {
                var raw = evRawType.GetValue(evt);
                bool down = raw.Equals(mouseDownValue);
                if (!down && !raw.Equals(mouseUpValue)) continue;

                int button = Convert.ToInt32(evButton.GetValue(evt));
                if (button < 0 || button > 2) continue;
                // Keep the original's view of which buttons changed this frame (Unity-side input layer)
                if (down ? !Input.GetMouseButtonDown(button) : !Input.GetMouseButtonUp(button)) continue;

                var pos = (System.Numerics.Vector2)evPos.GetValue(evt);
                var mods = EventModifiers.None;
                if ((bool)evShift.GetValue(evt)) mods |= EventModifiers.Shift;
                if ((bool)evControl.GetValue(evt)) mods |= EventModifiers.Control;
                if ((bool)evAlt.GetValue(evt)) mods |= EventModifiers.Alt;
                if ((bool)evCommand.GetValue(evt)) mods |= EventModifiers.Command;

                events.Add(new MouseEvt
                {
                    down = down,
                    button = button,
                    phys = new Vector2(pos.X, pos.Y),
                    clickCount = down ? Math.Max(1, (int)evClickCount.GetValue(evt)) : 0,
                    mods = mods
                });
                perButton[button]++;
            }

            for (int b = 0; b < 3; b++)
            {
                if (perButton[b] < 2) continue;
                if (perButton[b] == 2 && FirstOf(b, out bool firstDown) && firstDown)
                    continue; // press then release: the original order is already right
                return true;
            }

            events.Clear();
            return false;
        }

        static bool FirstOf(int button, out bool down)
        {
            foreach (var e in events)
                if (e.button == button) { down = e.down; return true; }
            down = false;
            return false;
        }

        // The original DispatchInput body, with its press loop and release loop replaced by one ordered loop
        static void Replay(object self, object host)
        {
            var vector = (Vector2)readPhysMouse.Invoke(null, new[] { host });
            var mods = (EventModifiers)readModifiers.Invoke(null, null);
            bool hasLast = (bool)hasLastPhysField.GetValue(self);
            var last = (Vector2)lastPhysField.GetValue(self);
            var delta = hasLast ? vector - last : Vector2.zero;
            mouseDeltaField.SetValue(self, delta);
            lastPhysField.SetValue(self, vector);
            hasLastPhysField.SetValue(self, true);

            float height = Convert.ToSingle(displayHeight.GetValue(host));
            // The original's position (ReadPhysMouse) and InputState's framebuffer position differ by a constant in a
            // resize frame (swapchain extent vs framebuffer height); carry that offset onto every event position
            var endFb = (System.Numerics.Vector2)stateMousePosition.GetValue(null);
            var offset = vector - new Vector2(endFb.X, endFb.Y);
            var dragger = Current.ProgramState == ProgramState.Playing ? Find.MapUI?.designatorManager?.Dragger : null;

            foreach (var e in events)
            {
                var phys = e.phys + offset;
                overrideMouse = new Vector2(phys.x, height - phys.y);
                // DesignationDragger caches its cells per Time.frameCount, which does not change between replayed
                // events; without this a second click in the frame would designate the first click's cells again
                if (dragger != null) dragger.lastUpdateFrame = -1;
                var type = e.down ? EventType.MouseDown : EventType.MouseUp;
                runPass.Invoke(self, new object[] { EventType.Layout, 0, phys, e.mods, false, delta });
                Event.current = new Event
                {
                    type = type,
                    rawType = type,
                    button = e.button,
                    clickCount = e.clickCount,
                    delta = delta,
                    modifiers = e.mods,
                    shift = (e.mods & EventModifiers.Shift) != 0,
                    alt = (e.mods & EventModifiers.Alt) != 0
                };
                dispatch.Invoke(self, new object[] { phys, false });
                if (!e.down && e.button == 0)
                    GUIUtility.hotControl = 0;
            }
            overrideMouse = null;

            if (logged < 3)
            {
                logged++;
                Log.Message($"Multiplayer: replayed {events.Count} mouse events of one frame in order (row 197, {logged}/3 logged)");
            }

            if (delta != Vector2.zero)
            {
                for (int j = 0; j < 3; j++)
                    if (Input.GetMouseButton(j) && !Input.GetMouseButtonDown(j))
                        runInputPass.Invoke(self, new object[] { EventType.MouseDrag, j, vector, mods });
            }

            dispatchKeyboard.Invoke(self, new object[] { vector, mods });

            var scroll = Input.mouseScrollDelta;
            if (scroll.x != 0f || scroll.y != 0f)
            {
                runPass.Invoke(self, new object[] { EventType.Layout, 0, vector, mods, false, delta });
                runScrollPass.Invoke(self, new object[] { vector, scroll, mods });
            }
        }
    }

    // Game code reads the mouse through UI.MousePositionOnUI (MouseMapPosition, MouseCell, drag box end, colonist bar
    // hits). During a replayed pass it must see that event's position, not the end-of-frame one.
    [HarmonyPatch(typeof(UI), nameof(UI.MousePositionOnUI), MethodType.Getter)]
    static class ImguiMouseReplayUiPosition
    {
        static bool Prefix(ref Vector2 __result)
        {
            if (ImguiMouseReplay.overrideMouse is not { } pos) return true;
            __result = pos / Prefs.UIScale;
            return false;
        }
    }
}
