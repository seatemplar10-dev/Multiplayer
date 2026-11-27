using System;
using System.Reflection;
using FluentAssertions;
using HarmonyLib;
using Multiplayer.Client.Util;

namespace Tests;

// FIXES row 556: a SyncMethod with 10+ parameters must get a working invoker.
public class InvokerRuleTest
{
    public static int Ten(int a, int b, int c, int d, int e, int f, int g, int h, int i, int j) => a + b + c + d + e + f + g + h + i + j;

    public static void TenRef(int a, int b, int c, int d, int e, int f, int g, int h, int i, ref int sum) => sum = a + b + c + d + e + f + g + h + i;

    public static int Three(int a, int b, int c) => a * b * c;

    public static void Throws(int a, int b, int c, int d, int e, int f, int g, int h, int i, int j) => throw new InvalidOperationException("inner");

    static MethodInfo M(string name) => typeof(InvokerRuleTest).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    static object[] Args10() => new object[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

    [Test]
    public void HarmonyHandlerFailsAtTenParams()
    {
        // known-bad arm: the Harmony bug this row works around; if Harmony fixes it this test says so
        Action act = () => HarmonyLib.MethodInvoker.GetHandler(M(nameof(Ten)));
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void FallbackInvokesTenParams()
    {
        var h = InvokerRule.Make(M(nameof(Ten)), () => true, out var used);
        used.Should().BeTrue();
        h(null, Args10()).Should().Be(55);
    }

    [Test]
    public void FallbackWritesRefBack()
    {
        var h = InvokerRule.Make(M(nameof(TenRef)), () => true, out _);
        var args = new object[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 0 };
        h(null, args);
        args[9].Should().Be(45);
    }

    [Test]
    public void FallbackDoesNotWrapExceptions()
    {
        var h = InvokerRule.Make(M(nameof(Throws)), () => true, out _);
        Action act = () => h(null, Args10());
        act.Should().Throw<InvalidOperationException>().WithMessage("inner");
    }

    [Test]
    public void FallbackOffKeepsOldThrow()
    {
        Action act = () => InvokerRule.Make(M(nameof(Ten)), () => false, out _);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ShortMethodKeepsHarmonyHandler()
    {
        var asked = false;
        var h = InvokerRule.Make(M(nameof(Three)), () => asked = true, out var used);
        used.Should().BeFalse();
        asked.Should().BeFalse();
        h(null, new object[] { 2, 3, 4 }).Should().Be(24);
    }
}
