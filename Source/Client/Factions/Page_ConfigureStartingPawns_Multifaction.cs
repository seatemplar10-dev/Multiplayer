using RimWorld;
using Verse;

namespace Multiplayer.Client.Factions
{
    internal class Page_ConfigureStartingPawns_Multifaction : Page_ConfigureStartingPawns
    {
        Faction _temporaryFactionForPawnCreation;
        Scenario _scenario;

        public override void PreOpen()
        {
            base.PreOpen();

            _scenario = Find.Scenario;

            GenerateTemporaryFaction();
            GeneratePawnsForConfigurePage();
        }

        public override void PostClose()
        {
            base.PostClose();

            CleanUpTemporaryFaction();

            // FIXES rows 236/237: closed without sending (DoCreateFaction restores on Next); "Back" reopens the ideology page first.
            if (!Find.WindowStack.IsOpen<Page_ChooseIdeo_Multifaction>())
                FactionSidebar.RestoreGameStateAfterPages();
        }

        public override void DoNext()
        {
            CleanUpTemporaryFaction();

            base.DoNext();
        }

        private void CleanUpTemporaryFaction()
        {
            if (_temporaryFactionForPawnCreation == null) return;

            Find.FactionManager.Remove(_temporaryFactionForPawnCreation);
            Find.FactionManager.toRemove.Remove(_temporaryFactionForPawnCreation);

            Current.Game.InitData.playerFaction = null;
            _temporaryFactionForPawnCreation = null;
        }

        private void GenerateTemporaryFaction()
        {
            _temporaryFactionForPawnCreation = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(_scenario.playerFaction.factionDef));
            _temporaryFactionForPawnCreation.temporary = true;

            Find.FactionManager.Add(_temporaryFactionForPawnCreation);
        }

        private void GeneratePawnsForConfigurePage()
        {
            var prevProgramState = Current.programStateInt;
            SetEntryProgramStateForCorrectPawnCreation();

            Current.Game.InitData.playerFaction = _temporaryFactionForPawnCreation;

            try
            {
                GeneratingPawns();
            }
            finally
            {       
                Current.programStateInt = prevProgramState;
            }
        }

        private static void SetEntryProgramStateForCorrectPawnCreation()
        {
            // Set ProgramState.Entry to ensure InInterface is false.
            // (#508 Tick-git)  InInterface seems unrelated to PawnCreation to me,
            //                  but it's necessary because it makes Faction.OfPlayer return
            //                  the temporary generated faction — which is required for proper apparel generation.

            Current.programStateInt = ProgramState.Entry;
        }

        private void GeneratingPawns()
        {
            _scenario.PostIdeoChosen();
        }
    }
}
