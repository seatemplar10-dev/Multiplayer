using System.Collections.Generic;

namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Server_InitDataRequest)]
public record struct ServerInitDataRequestPacket(bool includeConfigs) : IPacket
{
    public bool includeConfigs = includeConfigs;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref includeConfigs);
    }
}

[PacketDefinition(Packets.Client_InitData, allowFragmented: true)]
public record struct ClientInitDataPacket : IPacket
{
    // 1 MiB limit — large mod lists can exceed the default 32 KiB BindRemaining limit
    private const int MaxRawDataLength = 1 << 20;

    public string rwVersion;
    public int[] debugOnlySyncCmds;
    public int[] hostOnlySyncCmds;
    public RoundModeEnum modCtorRoundMode;
    public RoundModeEnum staticCtorRoundMode;
    public KeyedDefInfo[] defInfos;
    public bool includeConfigs;
    public byte[] rawMods;

    public List<ModData> Mods
    {
        get => ModData.ListBinder.Deserialize(rawMods);
        set => rawMods = ModData.ListBinder.Serialize(value);
    }

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref rwVersion);
        buf.Bind(ref debugOnlySyncCmds, BinderOf.Int());
        buf.Bind(ref hostOnlySyncCmds, BinderOf.Int());
        buf.BindEnum(ref modCtorRoundMode);
        buf.BindEnum(ref staticCtorRoundMode);
        buf.Bind(ref defInfos, BinderOf.Identity<KeyedDefInfo>());
        buf.Bind(ref includeConfigs);
        buf.BindRemaining(ref rawMods);
    }

    // Based on ContentSource but a byte, so smaller on the network and also doesn't use Verse
    // (which is unavailable in the standalone server)
    public enum ModSource : byte
    {
        Undefined,
        OfficialModsFolder,
        ModsFolder,
        SteamWorkshop,
    }

    public record struct ModData : IPacketBufferable
    {
        public string packageIdNonUnique;
        public string name;
        public ulong publishedFileId;
        public ModSource source;
        public List<ModFile> files;
        // Every settings file of the mod: one per Verse.Mod subclass with settings (RJW has five), plus HugsLib's
        // ModSettings for HugsLib. A single optional config dropped all but the first, so a joiner with the same
        // files always saw "Configs match: False" and Fix-and-restart applied only one of them.
        public List<ModConfig> configs;

        public void Bind(PacketBuffer buf)
        {
            buf.Bind(ref packageIdNonUnique);
            buf.Bind(ref name);
            buf.Bind(ref publishedFileId);
            buf.BindEnum(ref source);
            buf.Bind(ref files, BinderOf.Identity<ModFile>());
            if (buf.isWriting) configs ??= [];
            buf.Bind(ref configs, BinderOf.Identity<ModConfig>());
        }

        public static Binder<List<ModData>> ListBinder =>
            BinderOf.List(BinderOf.Identity<ModData>()).Gzipped(MaxRawDataLength);
    }

    public record struct ModFile : IPacketBufferable
    {
        public string path;
        public int hash;

        public void Bind(PacketBuffer buf)
        {
            buf.Bind(ref path);
            buf.Bind(ref hash);
        }
    }

    public record struct ModConfig : IPacketBufferable
    {
        public string fileName;
        public string contents;

        private const int MaxConfigContentLen = 8388608; // 8 MiB

        public void Bind(PacketBuffer buf)
        {
            buf.Bind(ref fileName);
            buf.Bind(ref contents, maxLength:MaxConfigContentLen);
        }
    }
}
