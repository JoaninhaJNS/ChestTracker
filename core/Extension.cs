using Xabbo;
using Xabbo.Core;
using Xabbo.Core.Events;
using Xabbo.Core.Game;
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
    private readonly RoomManager RoomManager;
    private FurniData? FurniData = null;
    private FurniInfo? StarterChest = null;
    private FurniInfo? NormalChest1 = null;
    private FurniInfo? NormalChest2 = null;
    private FurniInfo? CreditsChest1 = null;
    private FurniInfo? CreditsChest2 = null;
    private readonly HashSet<long> AllChestIds = [];
    private readonly HashSet<long> OpenChestIds = [];
    private readonly HashSet<long> InitializedChests = [];
    private readonly Dictionary<long, Dictionary<(bool, int), int>> ChestContents = [];
    private readonly Dictionary<long, string> ChestContentsCount = [];
    private readonly HashSet<long> CreditsChestIds = [];
    private readonly Dictionary<long, int> CoinsCount = [];
    private readonly Dictionary<int, Dictionary<(bool, int), int>> PendingChunks = [];
    private readonly SemaphoreSlim Throttle = new(1, 1);
    private DateTimeOffset LastSend = DateTimeOffset.MinValue;
    private string? CurrentRoomName = null;
    private long CurrentRoomId = 0;
    private int RoomVisitId = 0;
    private string CurrentRoomEntryTimestamp = "";
    private readonly List<string> Logs = [];

    public Extension() : base(new GEarthOptions
    {
        Name = "Chest Tracker",
        Description = "Tracks items in/out of chests",
        Author = "JoaninhaJNS",
        Version = "1.0.1"
    })
    {
        ext = this;
        RoomManager = new RoomManager(ext);
    }

    private FurniInfo? GetFurniInfo(bool isWall, int kind)
    {
        ItemType type = isWall ? ItemType.Wall : ItemType.Floor;
        if (FurniData is not null && FurniData.TryGetInfo(type, kind, out var info))
            return info;
        return null;
    }

    private void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string roomLabel = $"{CurrentRoomName ?? "Unknown"} ({CurrentRoomId})";
        Logs.Add($"{RoomVisitId}\0{roomLabel}\0{CurrentRoomEntryTimestamp}\0[{timestamp}] {message}");
    }

    private void Reset()
    {
        AllChestIds.Clear();
        OpenChestIds.Clear();
        InitializedChests.Clear();
        ChestContents.Clear();
        ChestContentsCount.Clear();
        CreditsChestIds.Clear();
        CoinsCount.Clear();
        PendingChunks.Clear();
    }

    protected override async void OnConnected(ConnectedEventArgs e)
    {
        base.OnConnected(e);
        await GameData.LoadAsync(e.Session.Hotel, [GameDataType.FurniData]);
        await GameData.WaitForLoadAsync();
        FurniData = GameData.Furni;

        if (FurniData is null) return;
        FurniInfo? GetClass(string identifier) =>
            FurniData.TryGetInfo(identifier, out var info) ? info : null;

        StarterChest = GetClass("wf_storage_furni_starter");
        NormalChest1 = GetClass("wf_storage_furni1");
        NormalChest2 = GetClass("wf_storage_furni2");
        CreditsChest1 = GetClass("wf_storage_coins1");
        CreditsChest2 = GetClass("wf_storage_coins2");

        Intercepted += ProcessPackets;
        ext.RoomManager.Entered += OnEnteredRoom;
    }

    protected override void OnDisconnected()
    {
        base.OnDisconnected();
        Reset();
        Logs.Clear();
        Intercepted -= ProcessPackets;
        ext.RoomManager.Entered -= OnEnteredRoom;
    }

    private void OnEnteredRoom(RoomEventArgs e)
    {
        IRoom? RoomInstance = e.Room;
        if (RoomInstance is null)
        {
            Console.WriteLine("Room instance returned null");
            return;
        }

        IRoomData? RoomData = RoomInstance.Data;
        if (RoomData is null)
        {
            Console.WriteLine("RoomData returned null");
            return;
        }

        CurrentRoomName = RoomData.Name;
        CurrentRoomId = RoomInstance.Id;
        CurrentRoomEntryTimestamp = DateTime.Now.ToString("HH:mm:ss");
        RoomVisitId++;
    }

    private void HandleRoomReady(Intercept _) => Reset();

    private void ProcessPackets(Intercept e)
    {
        try
        {
            if (e.Is(In.Objects))
            {
                HandleObjects(e);
                return;
            }

            if (e.Is(In.ObjectAdd))
            {
                HandleObjectAdd(e);
                return;
            }

            if (e.Is(In.ObjectDataUpdate))
            {
                HandleObjectDataUpdate(e);
                return;
            }

            if (e.Is(new Identifier(ClientType.Flash, Direction.In, "ItemsChestContentsChunk")))
            {
                HandleChestContents(e);
                return;
            }

            if (e.Is(In.RoomReady))
            {
                HandleRoomReady(e);
                return;
            }

            if (e.Is([Out.Chat, Out.Shout]))
            {
                HandleChat(e);
                return;
            }
        }
        catch { }
    }

    private string BuildLogs()
    {
        var lines = new List<string>();
        string? lastVisitId = null;

        foreach (string entry in Logs)
        {
            string[] parts = entry.Split('\0', 4);
            if (parts.Length < 4) continue;

            string visitId = parts[0];
            string roomLabel = parts[1];
            string entryTimestamp = parts[2];
            string log = parts[3];

            if (visitId != lastVisitId)
            {
                if (lines.Count > 0)
                {
                    lines.Add("");
                    lines.Add(new string('-', 78));
                    lines.Add("");
                }
                lines.Add($"[{entryTimestamp}] Room: {roomLabel}");
                lines.Add("");
                lastVisitId = visitId;
            }

            lines.Add(log);
        }

        return string.Join("\n", lines);
    }

    private void HandleChat(Intercept e)
    {
        string message = e.Packet.Read<string>();
        if (message.Equals(":chestlogs", StringComparison.OrdinalIgnoreCase))
        {
            e.Block();
            ext.Send(In.MOTDNotification, 1, Logs.Count == 0 ? "No chest logs yet." : BuildLogs());
        }
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

    private void RegisterItemChest(long id, string? contentsCount)
    {
        OpenChestIds.Add(id);
        ChestContentsCount[id] = contentsCount ?? "0";
        ScheduleSend(id);
    }

    private void RegisterCreditsChest(long id, string? contentsCount)
    {
        CreditsChestIds.Add(id);
        OpenChestIds.Add(id);
        ChestContentsCount[id] = contentsCount ?? "0";
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
        map.TryGetValue("contents_count", out string? contentsCount);

        if (isCredits)
        {
            RegisterCreditsChest(item.Id, contentsCount);
            return;
        }

        map.TryGetValue("everyone_can_open", out string? isOpen);
        if (isOpen != "1") return;

        RegisterItemChest(item.Id, contentsCount);
    }

    private void NotifyCreditsChestDiff(long chestId, int newCoins)
    {
        CoinsCount.TryGetValue(chestId, out int oldCoins);
        int diff = newCoins - oldCoins;
        CoinsCount[chestId] = newCoins;
        string action = diff > 0 ? "Added" : "Removed";
        string logMsg = $"[Credits] [Chest {chestId}] - {action} {Math.Abs(diff)}c";
        Log($"[CreditsChest {chestId}] - {action} {Math.Abs(diff)}c");
        ext.Send(In.NotificationDialog, "wired.error", 2,
            "message", $"[Chest {chestId}]\n\n{action} {Math.Abs(diff)}c",
            "image", "https://images.habbo.com/dcr/hof_furni/45508/CF_1_coin_bronze_icon.png");
    }

    private void HandleObjects(Intercept e)
    {
        IEnumerable<IFloorItem> floorItems = e.Packet.Read<FloorItemsMsg>();
        foreach (var item in floorItems)
            ProcessChest(item);
    }

    private void HandleObjectAdd(Intercept e)
    {
        IFloorItem item = e.Packet.Read<FloorItemAddedMsg>().Item;
        ProcessChest(item);
    }

    private void HandleObjectDataUpdate(Intercept e)
    {
        FloorItemDataUpdatedMsg? item = e.Packet.Read<FloorItemDataUpdatedMsg>();
        if (!AllChestIds.Contains(item.Id)) return;
        if (item.Data is not MapData map) return;

        map.TryGetValue("everyone_can_open", out string? isOpen);

        bool isKnownCreditsChest = CreditsChestIds.Contains(item.Id);
        if (isOpen != "1" && !isKnownCreditsChest) return;

        bool isNew = OpenChestIds.Add(item.Id);

        map.TryGetValue("contents_count", out string? newCountStr);
        newCountStr ??= "0";

        ChestContentsCount.TryGetValue(item.Id, out string? oldCountStr);
        oldCountStr ??= "0";
        ChestContentsCount[item.Id] = newCountStr;

        if (isNew)
        {
            ScheduleSend(item.Id);
            return;
        }

        if (!InitializedChests.Contains(item.Id)) return;
        if (newCountStr == oldCountStr) return;

        if (isKnownCreditsChest)
        {
            if (!int.TryParse(newCountStr, out int newCoins)) return;
            NotifyCreditsChestDiff(item.Id, newCoins);
        }
        else
        {
            ScheduleSend(item.Id);
        }
    }

    private void HandleChestContents(Intercept e)
    {
        PacketReader p = e.Packet.Reader();

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
            p.ReadInt(); // inventoryId
            p.ReadInt(); // lockState
            p.ReadInt(); // transactionId
            p.ReadSpan(4); // skip 4 bytes

            bool isWallItem = p.ReadBool();
            int kind = p.ReadInt();
            p.ReadString(); // legacyPosterId
            p.ReadBool();   // isGroupable
            p.ReadInt();    // specialType
            p.Parse<ItemData>(); // stuffData
            if (!isWallItem) p.ReadInt(); // extra (floor items only)

            var key = (isWallItem, kind);
            accumulated[key] = accumulated.GetValueOrDefault(key) + 1;
        }

        // wait for the last fragment before processing
        if (fragmentNo < totalFragments - 1) return;

        var newKindCounts = accumulated;
        PendingChunks.Remove(chestId);

        ChestContents.TryGetValue(chestId, out var oldKindCounts);
        oldKindCounts ??= [];

        // first chunk: store as baseline without notifying
        if (!InitializedChests.Contains(chestId))
        {
            e.Block();
            InitializedChests.Add(chestId);
            ChestContents[chestId] = newKindCounts;
            return;
        }

        // items whose count changed (added or partially removed)
        foreach (var ((isWall, kind), newQty) in newKindCounts)
        {
            oldKindCounts.TryGetValue((isWall, kind), out int oldQty);
            int diff = newQty - oldQty;
            if (diff == 0) continue;
            e.Block();
            FurniInfo? info = GetFurniInfo(isWall, kind);
            string name = info?.Name ?? $"kind:{kind}";
            string image = info is not null
                ? $"https://images.habbo.com/dcr/hof_furni/{info.Revision}/{info.Identifier}_icon.png"
                : "";
            string action = diff > 0 ? "Added" : "Removed";
            Log($"[ItemsChest {chestId}] - {action} {Math.Abs(diff)}x {name}");
            ext.Send(In.NotificationDialog, "wired.error", 2,
                "message", $"[Chest {chestId}]\n\n{action} [{Math.Abs(diff)}]\n\n{name}",
                "image", image);
        }

        // items that are no longer present at all (fully removed)
        foreach (var ((isWall, kind), oldQty) in oldKindCounts)
        {
            if (newKindCounts.ContainsKey((isWall, kind))) continue;
            e.Block();
            FurniInfo? info = GetFurniInfo(isWall, kind);
            string name = info?.Name ?? $"kind:{kind}";
            string image = info is not null
                ? $"https://images.habbo.com/dcr/hof_furni/{info.Revision}/{info.Identifier}_icon.png"
                : "";
            Log($"[ItemsChest {chestId}] - Removed {oldQty}x {name}");
            ext.Send(In.NotificationDialog, "wired.error", 2,
                "message", $"[Chest {chestId}]\n\nRemoved [{oldQty}]\n\n{name}",
                "image", image);
        }

        ChestContents[chestId] = newKindCounts;
    }
}