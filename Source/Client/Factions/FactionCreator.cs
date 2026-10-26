using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Multiplayer.API;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Factions;

public static class FactionCreator
{
    private static Dictionary<int, List<Pawn>> pawnStore = new();
    public static bool generatingMap;
    public static bool generatingIdeo; // FIXES row 192: IdeoFoundation.RandomizeIcon is skipped while set (see NewFactionWithIdeo)
    public const string preventSpecialCalculationPathInGenTicksTicksAbs = "dummy";

    public static void ClearData()
    {
        pawnStore.Clear();
    }

    [SyncMethod(exposeParameters = new[] { 1 })]
    public static void SendPawn(int playerId, Pawn p)
    {
        pawnStore.GetOrAddNew(playerId).Add(p);
    }

    [SyncMethod]
    public static void ChangeFactionColor(Faction faction, Color color)
    {
        if (faction is not { IsPlayer: true })
            return;

        faction.color = color;
    }

    [SyncMethod]
    public static void CreateFaction(int playerId, FactionCreationData creationData)
    {
        var executingOnlyOnIssuer = TickPatch.currentExecutingCmdIssuedBySelf;

        LongEventHandler.QueueLongEvent(() =>
        {
            var scenario = creationData.scenarioDef?.scenario ?? Current.Game.Scenario;
            Map newMap = null;
            Faction newFaction;

            // 1.7 lab, FIXES row 170 (runs FA3 / FA4): this long event runs outside any command context, i.e. in each PC's OWN faction
            // context (row 153), and MpScope.PushFaction only sets Faction.OfPlayer, not the faction's world data (research, policies,
            // history, storyteller: MultiplayerWorldComp.SetFaction). So InitNewGame's starting research went into each PC's own research
            // manager, and a FactionRepeater pop inside the scope could leave a PC's world data on the new faction after it (FA4: faction
            // 69's research progress in another insertion order on host and arbiter). The whole body now runs under ONE fixed faction with
            // its world data (the first real player faction: the same on every PC), the new faction's game init under a full push of the
            // new faction, and both are popped (world data back to this PC's own faction) before the issuer switches to the new faction.
            FactionExtensions.PushFaction(null, FixedContextFaction(), force: true);
            try
            {
                PrepareGameInitData(playerId, scenario, executingOnlyOnIssuer, creationData.startingPossessions);

                newFaction = NewFactionWithIdeo(
                    creationData.factionName,
                    creationData.factionColor,
                    scenario.playerFaction.factionDef,
                    creationData.chooseIdeoInfo
                );

                if (creationData.generateMap)
                    using (MpScope.PushFaction(newFaction))
                    {
                        foreach (var pawn in StartingPawnUtility.StartingAndOptionalPawns)
                            pawn.ideo.SetIdeo(newFaction.ideos.PrimaryIdeo);

                        newMap = GenerateNewMap(creationData.startingTile, scenario, creationData.setupNextMapFromTickZero);

                        // 1.7 lab (FIXES row 190, runs MF6 / MF7): mods queue map spawns with LongEventHandler.ExecuteWhenFinished during
                        // generation (VEF's GenerateMap postfix -> DoMapSpawns). Vanilla runs them after this event has ended, when
                        // Multiplayer.InInterface is true, so their things got ids from each PC's local counter (UniqueIdsPatch: a truck
                        // wreck was -4 on the host, -111 on the joiner) = a shared-list split on the new map. Run them here, still inside
                        // the event, where ids come from the synced UniqueIDsManager. Main thread only: the actions may touch graphics.
                        if (UnityData.IsInMainThread)
                            LongEventHandler.ForceExecuteToExecuteWhenFinished();
                    }

                foreach (Map map in Find.Maps)
                    foreach (var f in Find.FactionManager.AllFactions.Where(f => f.IsPlayer))
                        map.attackTargetsCache.Notify_FactionHostilityChanged(f, newFaction);

                FactionExtensions.PushFaction(null, newFaction, force: true);
                try
                {
                    InitNewGame(scenario);
                }
                finally
                {
                    FactionExtensions.PopFaction();
                }
            }
            finally
            {
                FactionExtensions.PopFaction();
            }

            if (executingOnlyOnIssuer)
            {
                ClearPortraitCacheFromPawnConfigPage();

                Current.Game.CurrentMap = newMap;

                Multiplayer.game.ChangeRealPlayerFaction(newFaction);

                InitLocalVisuals(scenario, newMap);

                // todo setting faction of self
                Multiplayer.Client.Send(new ClientSetFactionPacket(Multiplayer.session.playerId, newFaction.loadID));
            }
        }, "GeneratingMap", doAsynchronously: true, GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
    }

    // FIXES row 170: the first real (non-spectator) player faction in the faction list - the same faction on every PC
    private static Faction FixedContextFaction()
    {
        var spectator = Multiplayer.WorldComp?.spectatorFaction;
        return Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.IsPlayer && f != spectator) ?? Faction.OfPlayer;
    }

    private static void ClearPortraitCacheFromPawnConfigPage()
    {
        PortraitsCache.Clear();
    }

    private static void InitLocalVisuals(Scenario scenario, Map generatedMap)
    {
        // TODO #587: There may be other logical ScenParts (e.g., ScenPart_StartingResearch) that
        // need to be executed by all players in the game.
        var onlyVisualScenParts = new HashSet<Type>() { typeof(ScenPart_GameStartDialog) };

        // 1.7 lab (FIXES row 192, round 1): this runs on the issuer only, after the synced event. The intro dialog took an id from the
        // synced UniqueIDsManager (nextArchivedDialogID 1 vs 0) and was archived into the new faction's history on the issuer only.
        // Local ids here; ArchiveAddPatch drops archived dialogs with a negative id. The dialog itself still opens.
        bool prevLocalIds = Patches.UniqueIdsPatch.useLocalIdsOverride;
        try
        {
            Patches.UniqueIdsPatch.useLocalIdsOverride = true;
            PostGameStart(scenario, onlyVisualScenParts);
        }
        finally
        {
            Patches.UniqueIdsPatch.useLocalIdsOverride = prevLocalIds;
        }

        CameraJumper.TryJump(MapGenerator.playerStartSpotInt, generatedMap);
    }

    private static Map GenerateNewMap(PlanetTile tile, Scenario scenario, bool setupNextMapFromTickZero)
    {
        // This has to be null, otherwise, during map generation, Faction.OfPlayer returns it which breaks FactionContext
        Find.GameInitData.playerFaction = null;
        Find.GameInitData.PrepForMapGen();

        // ScenPart_PlayerFaction --> PreMapGenerate

        Settlement settlement = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
        settlement.Tile = tile;
        settlement.SetFaction(Faction.OfPlayer);
        Find.WorldObjects.Add(settlement);

        // ^^^^ Duplicate Code here ^^^^
        Scenario prevScenario = Find.Scenario;
        PlanetTile prevStartingTile = Find.GameInfo.startingTile;

        Current.Game.Scenario = scenario;
        Find.GameInfo.startingTile = tile;
        Find.GameInitData.startingTile = tile;

        MapSetup.SetupNextMapFromTickZero = setupNextMapFromTickZero;

        generatingMap = true;

        try
        {
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(
                tile,
                new IntVec3(250, 1, 250),
                null
            );

            return map;
        }
        finally
        {
            generatingMap = false;
            Current.Game.Scenario = prevScenario;
            Find.GameInfo.startingTile = prevStartingTile;
            Find.GameInitData.startingTile = prevStartingTile;
        }
    }

    private static void InitNewGame(Scenario scenario)
    {
        PawnUtility.GiveAllStartingPlayerPawnsThought(ThoughtDefOf.NewColonyOptimism);

        ResearchUtility.ApplyPlayerStartingResearch();

        // TODO #587: There may be other only visual ScenParts (e.g., ScenPart_GameStartDialog) that only need to
        // be executed by the local client (the issuer/creator of the new faction).
        PostGameStart(scenario, new HashSet<Type>() { typeof(ScenPart_StartingResearch) });

        // Initialize anomaly. Since the Anomaly comp is currently shared by all players,
        // we need to ensure that any new factions have access to anomaly research if
        // anomaly content was started.
        Find.ResearchManager.Notify_MonolithLevelChanged(Find.Anomaly.Level);
    }

    private static void PostGameStart(Scenario scenario, IEnumerable<Type> scenPartTypesToInvoke)
    {
        foreach (ScenPart part in scenario.AllParts)
        {
            if (scenPartTypesToInvoke.Contains(part.GetType()))
            {
                part.PostGameStart();
            }
        }
    }

    private static void PrepareGameInitData(int sessionId, Scenario scenario, bool self, List<ThingDefCount> startingPossessions)
    {
        if (!self)
        {
            Current.Game.InitData = new GameInitData()
            {
                startingPawnCount = GetStartingPawnsCountForScenario(scenario),
                gameToLoad = preventSpecialCalculationPathInGenTicksTicksAbs
            };
        }

        if (pawnStore.TryGetValue(sessionId, out var pawns))
        {
            Pawn firstPawn = pawns.First();

            GameInitData gameInitData = Current.Game.InitData;
            gameInitData.startingAndOptionalPawns = pawns;
            gameInitData.startingPossessions = new Dictionary<Pawn, List<ThingDefCount>>();

            foreach (var pawn in pawns)
            {
                gameInitData.startingPossessions[pawn] = new List<ThingDefCount>();
            }

            foreach (var possesion in startingPossessions)
            {
                gameInitData.startingPossessions[firstPawn].Add(possesion);
            }

            pawnStore.Remove(sessionId);
        }
    }

    private static Faction NewFactionWithIdeo(string name, Color color, FactionDef def, IdeologyData chooseIdeoInfo)
    {
        var faction = new Faction
        {
            loadID = Find.UniqueIDsManager.GetNextFactionID(),
            def = def,
            Name = name,
            color = color,
            hidden = true,
        };

        faction.ideos = new FactionIdeosTracker(faction);

        if (!ModsConfig.IdeologyActive || Find.IdeoManager.classicMode || chooseIdeoInfo.SelectedIdeo == null)
        {
            // 1.7 lab, FIXES row 153: this runs in a long event in each PC's own faction context, so Faction.OfPlayer is the
            // host's faction on the host and the spectator on a joiner (lab FA1: different gear, split at the first spawn).
            // Take the first real player faction in the list instead: the same faction on every PC.
            var spectator = Multiplayer.WorldComp?.spectatorFaction;
            var source = Find.FactionManager.AllFactionsListForReading
                .FirstOrDefault(f => f.IsPlayer && f != spectator && f.ideos?.PrimaryIdeo != null);
            faction.ideos.SetPrimary((source ?? Faction.OfPlayer).ideos.PrimaryIdeo);
        }
        else
        {
            Ideo newIdeo;
            try
            {
                generatingIdeo = true;
                newIdeo = GenerateIdeo(chooseIdeoInfo);
            }
            finally
            {
                generatingIdeo = false;
            }

            // 1.7 lab (FIXES row 192, round 1): vanilla picks the icon and colour in a deferred action (IdeoFoundation.RandomizeIcon ->
            // LongEventHandler.ExecuteWhenFinished), run later in this event after whatever else is queued; host and joiner got different
            // picks (RoundA/LightOrange vs Ideoligion_ToolsD/LightBlue). RandomizeIcon is skipped while generatingIdeo; the pick happens here
            // from a state seeded by the synced ideo id, with the same pools as vanilla.
            Rand.PushState(Gen.HashCombineInt(newIdeo.id, 0x5EA192));
            try
            {
                newIdeo.SetIcon(IdeoFoundation.GetRandomIconDef(newIdeo), IdeoFoundation.GetRandomColorDef(newIdeo));
            }
            finally
            {
                Rand.PopState();
            }

            faction.ideos.SetPrimary(newIdeo);
            Find.IdeoManager.Add(newIdeo);
        }

        foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
            faction.TryMakeInitialRelationsWith(other);

        Find.FactionManager.Add(faction);

        var newWorldFactionData = FactionWorldData.New(faction.loadID);
        Multiplayer.WorldComp.factionData[faction.loadID] = newWorldFactionData;
        newWorldFactionData.ReassignIds();

        // Add new faction to all maps (the new map is handled by map generation code)
        foreach (Map map in Find.Maps)
            MapSetup.InitNewFactionData(map, faction);

        foreach (var f in Find.FactionManager.AllFactions.Where(f => f.IsPlayer))
            if (f != faction)
                faction.SetRelation(new FactionRelation(f, FactionRelationKind.Neutral));

        return faction;
    }

    private static Ideo GenerateIdeo(IdeologyData chooseIdeoInfo)
    {
        List<MemeDef> list = chooseIdeoInfo.SelectedIdeo.memes.ToList();

        // 1.7 lab (FIXES row 203, live session 22 Oct 19:48): the same meme rule as vanilla Page_ChooseIdeoPreset.DoPreset (:537-545). Adding the
        // chosen (or a random) structure on top of a preset that already has one gave James's ideo two structures (Archist deityCount 0 +
        // RimDark Imperial 1 -> no deity, "Grammar unresolvable. Root 'salvation'", ERR: in the description).
        if (chooseIdeoInfo.SelectedStructure != null)
        {
            list.RemoveAll(m => m.category == MemeCategory.Structure);
            list.Add(chooseIdeoInfo.SelectedStructure);
        }
        else if (!list.Any(m => m.category == MemeCategory.Structure) && DefDatabase<MemeDef>.AllDefsListForReading.Where(m => m.category == MemeCategory.Structure && IdeoUtility.IsMemeAllowedFor(m, Find.Scenario.playerFaction.factionDef)).TryRandomElement(out var result))
            list.Add(result);

        Ideo ideo = IdeoGenerator.GenerateIdeo(new IdeoGenerationParms(Find.FactionManager.OfPlayer.def, forceNoExpansionIdeo: false, null, null, list, chooseIdeoInfo.SelectedIdeo.classicPlus, forceNoWeaponPreference: true));
        new Page_ChooseIdeoPreset { selectedStyles = chooseIdeoInfo.SelectedStyles }.ApplySelectedStylesToIdeo(ideo);

        return ideo;
    }

    public static int GetStartingPawnsCountForScenario(Scenario scenario)
    {
        foreach (ScenPart part in scenario.AllParts)
        {
            if (part is ScenPart_ConfigPage_ConfigureStartingPawnsBase startingPawnsConfig)
            {
                return startingPawnsConfig.TotalPawnCount;
            }
        }

        MpLog.Error("No starting pawns config found to access startingPawnCount");

        return 0;
    }
}
public record FactionCreationData : ISyncSimple
{
    public string factionName;
    public PlanetTile startingTile;
    public Color factionColor;
    [CanBeNull] public ScenarioDef scenarioDef;
    public IdeologyData chooseIdeoInfo;
    public bool generateMap;
    public List<ThingDefCount> startingPossessions;
    public bool setupNextMapFromTickZero;
}
