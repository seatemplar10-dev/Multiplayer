using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Multiplayer.Client.Util;
using Verse;

namespace Multiplayer.Client
{
    public static class ScribeUtil
    {
        private const string RootNode = "root";

        private static MemoryStream writingStream;

        public static SharedCrossRefs sharedCrossRefs => Multiplayer.game.sharedCrossRefs;
        public static LoadedObjectDirectory defaultCrossRefs;

        public static bool loading;

        //dbg
        public static bool removeMapRefs;
        private static List<ILoadReferenceable> removedMapRefs;

        public static void StartWriting(bool indent = false)
        {
            writingStream = new MemoryStream();

            Scribe.mode = LoadSaveMode.Saving;
            XmlWriterSettings xmlWriterSettings = new XmlWriterSettings()
            {
                Indent = indent,
                OmitXmlDeclaration = true
            };

            XmlWriter writer = XmlWriter.Create(writingStream, xmlWriterSettings);
            Scribe.saver.writer = writer;
            writer.WriteStartDocument();
        }

        public static byte[] FinishWriting()
        {
            Scribe.saver.FinalizeSaving();

            byte[] arr = writingStream.ToArray();
            writingStream.Close();
            writingStream = null;

            return arr;
        }

        public static void StartWritingToDoc()
        {
            Scribe.mode = LoadSaveMode.Saving;
            XmlWriter xmlWriter = new CustomXmlWriter();
            Scribe.saver.writer = xmlWriter;
            xmlWriter.WriteStartDocument();
        }

        public static XmlDocument FinishWritingToDoc()
        {
            var doc = ((CustomXmlWriter)Scribe.saver.writer).doc;
            Scribe.saver.FinalizeSaving();
            return doc;
        }

        public static void InitFromXmlDoc(XmlDocument doc)
        {
            ScribeMetaHeaderUtility.loadedGameVersion = VersionControl.CurrentVersionStringWithRev;

            Scribe.loader.curXmlParent = doc.DocumentElement;
            Scribe.mode = LoadSaveMode.LoadingVars;
        }

        public static void FinalizeLoading()
        {
            if (!loading)
            {
                Log.Error("Called FinalizeLoading() but we aren't loading");
                return;
            }

            ScribeLoader loader = Scribe.loader;

            try
            {
                Scribe.ExitNode();

                loader.curXmlParent = null;
                loader.curParent = null;
                loader.curPathRelToParent = null;
                loader.crossRefs.ResolveAllCrossReferences();
                loader.initer.DoAllPostLoadInits();
            }
            catch (Exception e)
            {
                Log.Error("Exception in FinalizeLoading(): " + e);
                loader.ForceStop();
                throw;
            }
            finally
            {
                loading = false;
            }
        }

        public static XmlDocument LoadDocument(byte[] data)
        {
            using var stream = new MemoryStream(data);
            using var reader = XmlReader.Create(stream);

            XmlDocument xmlDocument = new XmlDocument();
            xmlDocument.Load(reader);

            return xmlDocument;
        }

        public static byte[] XmlToByteArray(XmlNode node, string rootNode = null, bool indent = false)
        {
            XmlWriterSettings xmlWriterSettings = new XmlWriterSettings()
            {
                Indent = indent,
                OmitXmlDeclaration = true
            };

            using MemoryStream stream = new MemoryStream();
            using XmlWriter writer = XmlWriter.Create(stream, xmlWriterSettings);

            if (rootNode != null)
                writer.WriteStartElement(rootNode);

            node.WriteTo(writer);

            if (rootNode != null)
                writer.WriteEndElement();

            writer.Flush();
            return stream.ToArray();
        }

        public static void SupplyCrossRefs()
        {
            if (sharedCrossRefs == null) return;

            if (!loading)
            {
                Log.Warning("Tried to supply cross refs without calling ScribeUtil.StartLoading()");
                return;
            }

            defaultCrossRefs ??= Scribe.loader.crossRefs.loadedObjectDirectory;
            Scribe.loader.crossRefs.loadedObjectDirectory = sharedCrossRefs;

            if (removeMapRefs)
            {
                removedMapRefs = new List<ILoadReferenceable>();
                foreach (var map in Find.Maps)
                    removedMapRefs.AddRange(sharedCrossRefs.UnregisterAllFrom(map));
            }

            MpLog.Debug($"Cross ref supply: {sharedCrossRefs.allObjectsByLoadID.Count} {sharedCrossRefs.allObjectsByLoadID.LastOrDefault()} {Faction.OfPlayer}");
        }

        public static byte[] WriteExposable(IExposable element, string name = RootNode, bool indent = false, Action beforeElement = null)
        {
            StartWriting(indent);
            Scribe.EnterNode(RootNode);
            beforeElement?.Invoke();
            Scribe_Deep.Look(ref element, name);
            return FinishWriting();
        }

        // 1.7 lab (FIXES row 202, live session 22 Oct 19:48, James's Settle): SendPawn reads its pawn with ReadExposable inside the command,
        // where no long event runs, so LongEventHandler.ExecuteWhenFinished ran mods' "after loading" actions at once, in the middle of
        // Pawn.ExposeData (HAR's adult forced trait -> TraitSet.GainTrait read pawn.needs before the needs were loaded: NRE, postfixes
        // skipped). While ReadExposable runs, such actions are queued (LongEvents.cs DeferWhenFinishedDuringReadExposable) and run here
        // after FinalizeLoading, still inside the command, the same on every PC.
        internal static int readingExposable;
        internal static readonly List<Action> deferredWhenFinished = new();

        public static T ReadExposable<T>(byte[] data, Action<T> beforeFinish = null) where T : IExposable
        {
            loading = true;
            readingExposable++;
            int mark = deferredWhenFinished.Count;
            bool finished = false;
            T element = default;

            try
            {
                InitFromXmlDoc(LoadDocument(data));
                SupplyCrossRefs();

                Scribe_Deep.Look(ref element, RootNode);

                beforeFinish?.Invoke(element);

                if (removeMapRefs)
                {
                    sharedCrossRefs.Reregister(removedMapRefs);
                    removedMapRefs = null;
                }

                FinalizeLoading();
                finished = true;
            }
            finally
            {
                readingExposable--;
                if (!finished && deferredWhenFinished.Count > mark)
                {
                    // only this (possibly nested) read's own actions: they would run against a half-loaded object
                    Log.Warning($"MP: dropped {deferredWhenFinished.Count - mark} ExecuteWhenFinished action(s) queued while a ReadExposable failed (row 202)");
                    deferredWhenFinished.RemoveRange(mark, deferredWhenFinished.Count - mark);
                }
            }

            if (readingExposable == 0)
                RunDeferredWhenFinished();

            // Default cross refs restored in LoadedObjectsClearPatch

            return element;
        }

        static void RunDeferredWhenFinished()
        {
            if (deferredWhenFinished.Count == 0) return;

            var actions = deferredWhenFinished.ToList();
            deferredWhenFinished.Clear();

            foreach (var action in actions)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Error("Could not execute post-long-event action (deferred until Multiplayer's ReadExposable finished, row 202). Exception: " + e);
                }
            }
        }
    }
}
