using Xabbo;
using Xabbo.Core;
using Xabbo.Core.GameData;
using Xabbo.Core.Messages.Incoming;
using Xabbo.GEarth;
using Xabbo.Messages;
using Xabbo.Messages.Flash;

namespace ChestTracker;

public class Extension : GEarthExtension
{
    private readonly Extension ext;
    private readonly GameDataManager GameData = new();
    private FurniData? FurniData = null;
    private FurniInfo? StarterChest = null;
    private FurniInfo? NormalChest1 = null;
    private FurniInfo? NormalChest2 = null;
    private FurniInfo? CreditsChest1 = null;
    private FurniInfo? CreditsChest2 = null;
    private readonly HashSet<long> AllChestIds = [];
    private readonly HashSet<long> OpenChestIds = [];
    private readonly HashSet<long> InitializedChests = [];
    private readonly Dictionary<int, Dictionary<int, (bool IsWall, int Kind)>> ChestInventoryItems = [];
    private readonly HashSet<long> CreditsChestIds = [];
    private readonly Dictionary<long, int> CoinsCount = [];
    private readonly SemaphoreSlim Throttle = new(1, 1);
    private DateTimeOffset LastSend = DateTimeOffset.MinValue;
    private readonly Dictionary<int, Dictionary<int, (bool IsWall, int Kind)>> PendingChunks = [];

    public Extension() : base(new GEarthOptions
    {
        Name = "Chest Tracker",
        Description = "Track items in/out of chests",
        Author = "JoaninhaJNS",
        Version = "1.0.0"
    })
    {
        ext = this;
    }

    private FurniInfo? GetFurniInfo(bool isWall, int kind)
    {
        ItemType type = isWall ? ItemType.Wall : ItemType.Floor;
        if (FurniData is not null && FurniData.TryGetInfo(type, kind, out var info))
            return info;
        return null;
    }

    private void NotifyFurni(int chestId, string action, int qty, bool isWall, int kind)
    {
        var info = GetFurniInfo(isWall, kind);
        string name = info?.Name ?? $"kind:{kind}";
        string image = info is not null
            ? $"https://images.habbo.com/dcr/hof_furni/{info.Revision}/{info.Identifier}_icon.png"
            : "";
        ext.Send(In.NotificationDialog, "wired.error", 2,
            "message", $"[Chest {chestId}]\n\n{action} [{qty}]\n\n{name}",
            "image", image);
    }

    private void Reset()
    {
        AllChestIds.Clear();
        OpenChestIds.Clear();
        InitializedChests.Clear();
        ChestInventoryItems.Clear();
        PendingChunks.Clear();
        CreditsChestIds.Clear();
        CoinsCount.Clear();
    }

    private void ScheduleSend(long chestId)
    {
        _ = Task.Run(async () =>
        {
            await Throttle.WaitAsync();
            try
            {
                TimeSpan elapsed = DateTimeOffset.UtcNow - LastSend;
                TimeSpan wait = TimeSpan.FromMilliseconds(500) - elapsed;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait);

                ext.Send((ClientType.Flash, Direction.Out, "OpenChestAndGetContents"), (int)chestId);
                LastSend = DateTimeOffset.UtcNow;
            }
            finally
            {
                Throttle.Release();
            }
        });
    }

    private void RegisterItemChest(long id)
    {
        OpenChestIds.Add(id);
        ScheduleSend(id);
    }

    private void RegisterCreditsChest(long id, string? contentsCount)
    {
        CreditsChestIds.Add(id);
        OpenChestIds.Add(id);

        if (int.TryParse(contentsCount, out int coins))
            CoinsCount[id] = coins;

        InitializedChests.Add(id);
    }

    private void ProcessChest(IFloorItem item)
    {
        bool isCredits =
            item.Kind == CreditsChest1?.Kind ||
            item.Kind == CreditsChest2?.Kind;

        bool isChest = isCredits ||
            item.Kind == StarterChest?.Kind ||
            item.Kind == NormalChest1?.Kind ||
            item.Kind == NormalChest2?.Kind;

        if (!isChest) return;
        if (item.Data is not MapData map) return;
        AllChestIds.Add(item.Id);

        if (isCredits)
        {
            if (!map.TryGetValue("contents_count", out string? contentsCount)) return;
            RegisterCreditsChest(item.Id, contentsCount);
            return;
        }

        if (!map.TryGetValue("everyone_can_open", out string? isOpen)) return;
        if (isOpen != "1") return;
        RegisterItemChest(item.Id);
    }

    protected override async void OnConnected(ConnectedEventArgs e)
    {
        base.OnConnected(e);
        await GameData.LoadAsync(e.Session.Hotel, [GameDataType.FurniData]);
        await GameData.WaitForLoadAsync();
        FurniData = GameData.Furni;
        if (FurniData is null) return;
        FurniInfo? GetClass(string identifier) => FurniData.TryGetInfo(identifier, out var info) ? info : null;
        StarterChest = GetClass("wf_storage_furni_starter");
        NormalChest1 = GetClass("wf_storage_furni1");
        NormalChest2 = GetClass("wf_storage_furni2");
        CreditsChest1 = GetClass("wf_storage_coins1");
        CreditsChest2 = GetClass("wf_storage_coins2");
        Intercepted += ProcessPackets;
    }

    protected override void OnDisconnected()
    {
        base.OnDisconnected();
        Reset();
        Intercepted -= ProcessPackets;
    }

    private void ProcessPackets(Intercept e)
    {
        try
        {
            if (e.Is(In.Objects)) { HandleObjects(e); return; }
            if (e.Is(In.ObjectAdd)) { HandleObjectAdd(e); return; }
            if (e.Is(In.ObjectDataUpdate)) { HandleObjectDataUpdate(e); return; }
            if (e.Is(In.RoomReady)) { HandleRoomReady(e); return; }
            if (e.Is(new Identifier(ClientType.Flash, Direction.In, "ItemsChestContentsChunk"))) { HandleChestContents(e); return; }
            if (e.Is(new Identifier(ClientType.Flash, Direction.In, "ItemsChestContentsUpdated"))) { HandleChestContentsUpdated(e); return; }
        }
        catch { }
    }

    private void HandleRoomReady(Intercept e) => Reset();
    private void HandleObjects(Intercept e)
    {
        var floorItems = e.Packet.Read<FloorItemsMsg>();

        foreach (var item in floorItems)
            ProcessChest(item);
    }

    private void HandleObjectAdd(Intercept e)
    {
        var item = e.Packet.Read<FloorItemAddedMsg>().Item;
        ProcessChest(item);
    }

    private void HandleObjectDataUpdate(Intercept e)
    {
        var item = e.Packet.Read<FloorItemDataUpdatedMsg>();

        if (!AllChestIds.Contains(item.Id)) return;
        if (item.Data is not MapData map) return;
        if (!map.TryGetValue("everyone_can_open", out string? isOpen)) return;
        bool isKnownCreditsChest = CreditsChestIds.Contains(item.Id);
        if (isOpen != "1" && !isKnownCreditsChest) return;
        bool isNew = OpenChestIds.Add(item.Id);

        if (isNew)
        {
            ScheduleSend(item.Id);
            return;
        }

        if (!isKnownCreditsChest) return;
        if (!InitializedChests.Contains(item.Id)) return;
        if (!map.TryGetValue("contents_count", out string? newCountStr)) return;
        if (!int.TryParse(newCountStr, out int newCoins)) return;
        if (!CoinsCount.TryGetValue(item.Id, out int oldCoins)) return;
        if (newCoins == oldCoins) return;
        int diff = newCoins - oldCoins;
        CoinsCount[item.Id] = newCoins;
        string action = diff > 0 ? "Added" : "Removed";
        ext.Send(In.NotificationDialog, "wired.error", 2,
            "message", $"[Chest {item.Id}]\n\n{action} {Math.Abs(diff)}c",
            "image", "https://images.habbo.com/dcr/hof_furni/45508/CF_1_coin_bronze_icon.png");
    }

    private void HandleChestContents(Intercept e)
    {
        var p = e.Packet.Reader();
        int chestId = p.ReadInt();
        int totalFragments = p.ReadInt();
        int fragmentNo = p.ReadInt();
        int count = p.ReadInt();

        if (!PendingChunks.TryGetValue(chestId, out var accumulated))
        {
            accumulated = [];
            PendingChunks[chestId] = accumulated;
        }

        for (int i = 0; i < count; i++)
        {
            int inventoryId = p.ReadInt();
            p.ReadInt(); // lockState
            p.ReadInt(); // transactionId
            p.ReadSpan(4);

            bool isWallItem = p.ReadBool();
            int kind = p.ReadInt();
            p.ReadString(); // legacyPosterId
            p.ReadBool();   // isGroupable
            p.ReadInt();    // specialType
            p.Parse<ItemData>(); // stuffData
            if (!isWallItem) p.ReadInt(); // extra (floor items only)

            accumulated[inventoryId] = (isWallItem, kind);
        }

        if (fragmentNo < totalFragments - 1) return;

        if (InitializedChests.Contains(chestId))
        {
            PendingChunks.Remove(chestId);
            return;
        }

        e.Block();
        InitializedChests.Add(chestId);
        ChestInventoryItems[chestId] = accumulated;
        PendingChunks.Remove(chestId);
    }
    private void HandleChestContentsUpdated(Intercept e)
    {
        var p = e.Packet.Reader();
        int chestId = p.ReadInt();
        if (!InitializedChests.Contains(chestId)) return;
        if (!ChestInventoryItems.TryGetValue(chestId, out var chestMap)) return;
        var removedByKind = new Dictionary<(bool IsWall, int Kind), int>();
        int removedCount = p.ReadInt();

        for (int i = 0; i < removedCount; i++)
        {
            int removedInventoryId = p.ReadInt();
            if (chestMap.TryGetValue(removedInventoryId, out var removed))
            {
                chestMap.Remove(removedInventoryId);
                var key = (removed.IsWall, removed.Kind);
                removedByKind[key] = removedByKind.GetValueOrDefault(key) + 1;
            }
        }

        var addedByKind = new Dictionary<(bool IsWall, int Kind), int>();
        int addedCount = p.ReadInt();

        for (int i = 0; i < addedCount; i++)
        {
            int inventoryId = p.ReadInt();
            p.ReadInt(); // lockState
            p.ReadInt(); // transactionId
            p.ReadSpan(4);
            bool isWallItem = p.ReadBool();
            int kind = p.ReadInt();
            p.ReadString(); // legacyPosterId
            p.ReadBool();   // isGroupable
            p.ReadInt();    // specialType
            p.Parse<ItemData>(); // stuffData
            if (!isWallItem) p.ReadInt(); // extra (floor items only)
            chestMap[inventoryId] = (isWallItem, kind);
            var key = (isWallItem, kind);
            addedByKind[key] = addedByKind.GetValueOrDefault(key) + 1;
        }

        foreach (var ((isWall, kind), qty) in removedByKind)
            NotifyFurni(chestId, "Removed", qty, isWall, kind);

        foreach (var ((isWall, kind), qty) in addedByKind)
            NotifyFurni(chestId, "Added", qty, isWall, kind);
    }
}