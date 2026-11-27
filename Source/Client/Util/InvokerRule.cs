using System;
using System.Reflection;
using HarmonyLib;

namespace Multiplayer.Client.Util;

// FIXES row 556 (28 Nov): Harmony's MethodInvoker.GetHandler throws ArgumentException "opcode" for a method with 10 or more
// parameters (rows 533, 535, cu1 Planning Extended, cu3 Panhandling, Achtung's own ApplySettings). Every SyncMethod is built with it,
// so such a method failed to register and ran unsynced on each PC. The fallback calls the method through reflection: same arguments,
// ref/out values written back into the array, exceptions not wrapped. No game types here so the unit tests can link this file.
public static class InvokerRule
{
    public static FastInvokeHandler Make(MethodInfo method, Func<bool> fallbackOn, out bool usedFallback)
    {
        usedFallback = false;
        try
        {
            return HarmonyLib.MethodInvoker.GetHandler(method);
        }
        catch (ArgumentException) when (fallbackOn())
        {
            usedFallback = true;
            return (target, args) => method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, args, null);
        }
    }
}
