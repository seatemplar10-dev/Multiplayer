using JetBrains.Annotations;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    public class DesyncedWindow : Window
    {
        const int NumButtons = 5;
        const float ButtonsWidth = 120 * NumButtons + 10 * (NumButtons - 1);

        public override Vector2 InitialSize => new(30 + 130 * NumButtons, 110);

        private string text;
        private readonly SaveableDesyncInfo desyncInfo;
        private float openedAt;
        private bool infoWritten;
        private bool rejoining;
        [CanBeNull] private SaveableDesyncInfo.HostInfo hostInfo;

        public DesyncedWindow(string text, SaveableDesyncInfo desyncInfo)
        {
            this.text = text;
            this.desyncInfo = desyncInfo;

            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = false;
            absorbInputAroundWindow = true;
            openedAt = Time.realtimeSinceStartup;

            layer = WindowLayer.Super;

#if DEBUG
            doCloseX = true;
#endif
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;

            Text.Anchor = TextAnchor.UpperCenter;
            var label = "MpDesynced".Translate();
            if (MpVersion.IsDebug || Prefs.DevMode) label += "\n" + text;
            else label += "\n" + "MpDesyncedSubtitle".Translate();
            Widgets.Label(new Rect(0, 0, inRect.width, 40), label);
            Text.Anchor = TextAnchor.UpperLeft;

            var buttonsRect = new Rect((inRect.width - ButtonsWidth) / 2, 40, ButtonsWidth, 35);

            GUI.BeginGroup(buttonsRect);

            float x = 0;
            // FIXES row 447: no resync until the desync report is written (or 30 s), W446's continuation is the backup
            var gate = ResyncGate.State(desyncInfo.Written, Time.realtimeSinceStartup - openedAt);
            var resyncLabel = gate.Enabled ? "MpTryResync".Translate().ToString() : gate.Label;
            var resyncClicked = Widgets.ButtonText(new Rect(x, 0, 120, 35), resyncLabel, active: gate.Enabled);
            if (resyncClicked && gate.Enabled && !rejoining)
            {
                rejoining = true;
                if (!LabOff.Row(446)) desyncInfo.WriteWhenReady(() => hostInfo); // FIXES row 446
                Log.Message("Multiplayer: requesting rejoin");
                Rejoiner.DoRejoin();
            }

            x += 120 + 10;

            if (Widgets.ButtonText(new Rect(x, 0, 120, 35), "Save".Translate()))
                Find.WindowStack.Add(new SaveGameWindow(Multiplayer.session.gameName) { layer = WindowLayer.Super });
            x += 120 + 10;

            if (Widgets.ButtonText(new Rect(x, 0, 120, 35), "MpChatButton".Translate()))
                Find.WindowStack.Add(new ChatWindow
                {
                    closeOnClickedOutside = true,
                    absorbInputAroundWindow = true,
                    saveSize = false,
                    layer = WindowLayer.Super
                });
            x += 120 + 10;

            var openDesyncsFolder = MpUI.ButtonTextWithTip(
                new Rect(x, 0, 120, 35),
                "MpOpenDesyncFolder".Translate(),
                infoWritten ? null : "MpDesyncedWaiting".Translate() + MpUI.FixedEllipsis(), !infoWritten, 9793021
            );
            if (openDesyncsFolder)
                ShellOpenDirectory.Execute(Multiplayer.DesyncsDir);
            x += 120 + 10;

            if (Widgets.ButtonText(new Rect(x, 0, 120, 35), "Quit".Translate()))
                MainMenuPatch.AskQuitToMainMenu();

            GUI.EndGroup();
        }

        public void HandleHostDesyncInfo(SaveableDesyncInfo.HostInfo hostInfo)
        {
            if (infoWritten)
                Log.Warning("Multiplayer: the other side's desync traces arrived after the desync report was written; " +
                            "they are not in it");
            this.hostInfo = hostInfo;
        }

        public override void WindowUpdate()
        {
            // The other side's traces come back through the server; from the arbiter (a second, headless process) when
            // it is Playing, which can take longer than from the in-process host
            var maxWait = Multiplayer.session?.ArbiterPlaying == true ? 15f : 5f;

            var shouldWrite = hostInfo != null || Time.realtimeSinceStartup - openedAt > maxWait;
            if (!infoWritten && shouldWrite && desyncInfo.ReadyToSave)
            {
                desyncInfo.TryWriteOnce(hostInfo);
                infoWritten = true;
            }
        }

        // FIXES row 446: the window closing (Try resync, Quit, a newer desync clearing the window stack) must not lose
        // the report; if it is not written yet, it is written when the two report tasks end
        public override void PostClose()
        {
            if (!infoWritten && !LabOff.Row(446))
                desyncInfo.WriteWhenReady(() => hostInfo);
        }

        // FIXES row 446: Rejoiner.DoRejoin and MpUI.ClearWindowStack drop windows without PostClose; arm every open
        // DesyncedWindow first (WriteWhenReady is idempotent). -mplaboff=446 skips it (old behaviour).
        public static void WriteOpenReports()
        {
            if (LabOff.Row(446) || Find.WindowStack == null) return;
            foreach (var w in Find.WindowStack.windows.ToArray())
                if (w is DesyncedWindow dw && !dw.infoWritten)
                    dw.desyncInfo.WriteWhenReady(() => dw.hostInfo);
        }
    }

}
