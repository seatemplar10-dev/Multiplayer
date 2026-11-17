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
        pawnParts.Clear();
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

                        newMap = GenerateNewMap(creationData.startingTile, scenario, creationData.setupNextMapFromTickZero, creationData.customXenotypes);

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

    // FIXES row 385: GameInitData.PrepForMapGen ends with a loop over StartingPawnUtility.GetGenerationRequest(i).ForcedCustomXenotype
    // (Verse/GameInitData.cs:134) that adds each custom xenotype picked on the pawn page to Current.Game.customXenotypeDatabase. The
    // requests exist on the issuing PC only (the page's static list), so the database, which is saved and read by the simulation
    // (PawnGenerator.AdjustXenotypeForFactionlessPawn: customXenotypes.Count in a Rand.Range, TryRandomElement; Pawn_GeneTracker:93),
    // got the entry on the issuer and not on the other PCs or the arbiter. The issuer now ships the xenotypes in the command; every PC
    // drops what PrepForMapGen added from its local requests and adds the carried ones (same order, no Rand).
    private static void PrepForMapGenWithCarriedXenotypes(List<CarriedCustomXenotype> carried)
    {
        var database = Current.Game.customXenotypeDatabase?.customXenotypes;
        var before = database?.ToList();

        Find.GameInitData.PrepForMapGen();

        if (database == null)
            return;

        database.Clear();
        database.AddRange(before);

        if (carried == null)
            return;

        foreach (var data in carried)
        {
            if (database.Any(x => x.name == data.Name && x.inheritable == data.Inheritable && x.iconDef == data.IconDef && x.genes.SequenceEqual(data.Genes)))
                continue;

            database.Add(new CustomXenotype
            {
                name = data.Name,
                inheritable = data.Inheritable,
                iconDef = data.IconDef,
                genes = data.Genes.ToList()
            });
            MpLog.Log($"Multiplayer: carried custom xenotype '{data.Name}' in CreateFaction (row 385)");
        }
    }

    // FIXES row 385: the distinct custom xenotypes of the first pawnCount starting pawn requests (what PrepForMapGen reads), on the issuer
    public static List<CarriedCustomXenotype> CollectCustomXenotypes(int pawnCount)
    {
        var result = new List<CarriedCustomXenotype>();
        if (!ModsConfig.BiotechActive)
            return result;

        var seen = new List<CustomXenotype>();
        for (int i = 0; i < pawnCount; i++)
        {
            var xenotype = StartingPawnUtility.GetGenerationRequest(i).ForcedCustomXenotype;
            if (xenotype == null || seen.Contains(xenotype))
                continue;

            seen.Add(xenotype);
            result.Add(new CarriedCustomXenotype(xenotype.name, xenotype.inheritable, xenotype.iconDef, xenotype.genes.ToList()));
        }

        return result;
    }

    private static Map GenerateNewMap(PlanetTile tile, Scenario scenario, bool setupNextMapFromTickZero, List<CarriedCustomXenotype> carriedXenotypes)
    {
        // This has to be null, otherwise, during map generation, Faction.OfPlayer returns it which breaks FactionContext
        Find.GameInitData.playerFaction = null;
        PrepForMapGenWithCarriedXenotypes(carriedXenotypes);

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
                newIdeo = GenerateIdeo(chooseIdeoInfo, LabOff.Row(236) ? Find.Scenario.playerFaction.factionDef : def);
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

    // FIXES row 236: factionDef = the event's scenario's player faction (the same on every PC), not Find.Scenario, which the
    // configuration pages had replaced on the issuer only.
    private static Ideo GenerateIdeo(IdeologyData chooseIdeoInfo, FactionDef factionDef)
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
        else if (!list.Any(m => m.category == MemeCategory.Structure) && DefDatabase<MemeDef>.AllDefsListForReading.Where(m => m.category == MemeCategory.Structure && IdeoUtility.IsMemeAllowedFor(m, factionDef)).TryRandomElement(out var result))
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

    // 1.7 lab (FIXES row 421, lab run hc74c 17 Nov 23:15:54, client Settle with the Character Editor pawn 'Sea'): SendPawn ships the pawn's
    // whole save XML inside ONE sync command, and a command holds at most 65535 bytes on the way up (ClientCommandPacket.Bind) and on the
    // way down (ServerCommandPacket.Bind; ConnectionBase.MaxSinglePacketSize 65536). The pawn was 70299 bytes: "WriterException: Remaining
    // bytes too big (70299>65535)" on the issuer, nothing sent, the Settle died. A pawn whose XML is over PawnPartSize now goes as several
    // SendPawnPart commands. One PC's commands keep their order through the server, so every PC gets the parts in the same order and reads
    // the pawn inside the last part's command with the same reader SendPawn's exposed argument uses. Smaller pawns go the old way.
    // lab switch row 421 (18 Nov): off = old behaviour (always the one SendPawn command)
    public const int PawnPartSize = 32768; // a byte[] argument holds under 65535 bytes (SyncSerialization array length is a ushort)
    // Opus review 17 Nov: every pawn that fits in one command keeps the old SendPawn path. 512 bytes leave room for the command header
    // (method id, playerId, the byte length prefix; about 16 bytes measured from source).
    public const int OneCommandMaxXml = 65535 - 512;
    private static Dictionary<int, List<byte[]>> pawnParts = new();

    public static void SendPawnAnySize(int playerId, Pawn p)
    {
        if (LabOff.Row(421))
        {
            SendPawn(playerId, p);
            return;
        }

        byte[] xml = ScribeUtil.WriteExposable(p);
        if (xml.Length <= OneCommandMaxXml)
        {
            SendPawn(playerId, p);
            return;
        }

        int count = (xml.Length + PawnPartSize - 1) / PawnPartSize;
        Log.Message($"MP: starting pawn {p} is {xml.Length} bytes, sent in {count} parts (row 421)");
        for (int i = 0; i < count; i++)
        {
            int start = i * PawnPartSize;
            byte[] part = new byte[Math.Min(PawnPartSize, xml.Length - start)];
            Array.Copy(xml, start, part, 0, part.Length);
            SendPawnPart(playerId, i, count, part);
        }
    }

    [SyncMethod]
    public static void SendPawnPart(int playerId, int index, int count, byte[] part)
    {
        if (index == 0)
            pawnParts.Remove(playerId); // a new pawn starts: drop a half-received one

        var parts = pawnParts.GetOrAddNew(playerId);
        if (part == null || count < 1 || index != parts.Count || index >= count)
        {
            pawnParts.Remove(playerId);
            Log.Error($"MP: starting pawn part {index}/{count} of player {playerId} is out of order, pawn dropped (row 421)");
            return;
        }

        parts.Add(part);
        if (parts.Count < count)
            return;

        pawnParts.Remove(playerId);

        int total = 0;
        foreach (var b in parts)
            total += b.Length;

        byte[] xml = new byte[total];
        int at = 0;
        foreach (var b in parts)
        {
            Array.Copy(b, 0, xml, at, b.Length);
            at += b.Length;
        }

        pawnStore.GetOrAddNew(playerId).Add((Pawn)ExposableSerialization.ReadExposable(typeof(Pawn), xml));
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
    public List<CarriedCustomXenotype> customXenotypes; // FIXES row 385
}

// FIXES row 385: a CustomXenotype by value (fileName is a local file name and is not carried)
public record CarriedCustomXenotype(string Name, bool Inheritable, XenotypeIconDef IconDef, List<GeneDef> Genes) : ISyncSimple;
