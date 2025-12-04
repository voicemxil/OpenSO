using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.RC;
using FSO.LotView.Model;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.Lots;
using FSO.Server.Database.DA.Objects;
using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.NetPlay.EODs.Handlers.Data;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.SimAntics.Primitives;
using Newtonsoft.Json;
using NLog;
using static Mysqlx.Notice.Warning.Types;

namespace FSO.Server
{
    internal class ToolPluginAnonymize : ITool
    {
        private PluginAnonymizeOptions Options;
        private static Logger LOG = LogManager.GetCurrentClassLogger();
        private IDAFactory DAFactory;

        private ServerConfiguration Config;
        public ToolPluginAnonymize(PluginAnonymizeOptions options, ServerConfiguration config, IDAFactory daFactory)
        {
            Options = options;
            Config = config;
            DAFactory = daFactory;
        }

        private class PluginJson
        {
            [JsonProperty("objectID")]
            public uint ObjectID { get; set; }
            [JsonProperty("isReachable")]
            public bool IsReachable { get; set; } = false;
            [JsonProperty("delete")]
            public bool Delete { get; set; } = false;
        }

        private class SignPluginJson : PluginJson
        {
            [JsonProperty("signFlags")]
            public uint SignFlags { get; set; }
            [JsonProperty("message")]
            public string Message { get; set; }
        }

        private class CardPluginJson : PluginJson
        {
            [JsonProperty("title")]
            public string Title { get; set; }
            [JsonProperty("description")]
            public string Description { get; set; }
            [JsonProperty("cardContents")]
            public string[] CardContents { get; set; }
        }

        private class HouseJson
        {
            [JsonProperty("houseName")]
            public string HouseName { get; set; }
            [JsonProperty("houseId")]
            public uint HouseId { get; set; }
            [JsonProperty("houseAdmitMode")]
            public int HouseAdmitMode { get; set; }

            [JsonProperty("signs")]
            public SignPluginJson[] Signs { get; set; }
            [JsonProperty("cards")]
            public CardPluginJson[] Cards { get; set; }
        }

        private class ReviewJson
        {
            [JsonProperty("publicHouses")]
            public HouseJson[] PublicHouses; // Admit all, ban list
            [JsonProperty("privateHouses")]
            public HouseJson[] PrivateHouses; // Admit list, ban all
        }

        private const uint SIGN_PLUGIN = 0x2a6356a0;
        private const uint DRAW_CARD_PLUGIN = 0x895C1CEB;
        private const uint PERMISSION_DOOR_PLUGIN = 0x0A69F29F;

        // There's not really a brilliant way of getting all the objects that use the plugin type,
        // So here's all the ones we expect with base content.
        private static uint[] SignTypes = [
            0xA92EFE75, // Rustic
            0x99F6D314, // Sandwich board
            0xFFEEA490, // Sci-fi
            0xE86BB6D7, // Shop
            0xDCECE8AA, // Theater
            0x23295F48, // robotfactory
            0xD067F355, // Warning
            0xA996978A, // Conference
            0x70BD99F7, // Corkboard
            0xEB402C8A, // Holiday
            0x5700D1C5, // Landmark
            0xBFBB8152, // Neon
            0xA9B78F1D, // Chalkboard L (unused?)
            0xA9A6DF80, // Chalkboard R (unused?)

            // FSO CC
            0x7E055DC7, // Lucky Folding Write Board
            0x59896C56, // Leaf Note Sign
            0x4BA28DDD, // Postcards
            0x2CB89BF8, // Halloween Sign
            0x2C47F9F4, // Chalk it down
            0x584B4823, // Chalk it up
            ];

        private const uint DRAW_A_CARD_TYPE = 0x34E956FE;
        private const uint TELEPORTER_TYPE = 0x96A776CE;
        private const int TELEPORT_INTERACTION = 8;
        public static readonly int TICKRATE = 30;

        private const string DOOR_GLOBALS = "DoorGlobals";
        private const string TELEPORT_START_ANIM = "a20-teleporter-step-in";
        private const string TELEPORT_FAIL_ANIM = "a20-teleporter-check-self-insideout";

        // If the teleporter start animation plays, it is reachable.
        // If the interaction finishes after this and the fail animation plays, then the teleporter is obstructed
        // If the interaction fihishes after this and the fail animation doesn't play, then the teleporter works.

        private bool AdmitModePublic(int admitMode)
        {
            return !(admitMode == 1 || admitMode == 3); // admit list, ban all
        }

        private HashSet<int> GetUniqueLots(List<DbObject> objects)
        {
            var result = new HashSet<int>();

            foreach (var obj in objects)
            {
                if (obj.lot_id.HasValue)
                {
                    result.Add(obj.lot_id.Value);
                }
            }

            return result;
        }

        private void CleanLot(VM Lot)
        {
            var avatars = new List<VMEntity>(Lot.Entities.Where(x => x is VMAvatar && x.PersistID != 0));
            //step 1, force everyone to leave.
            foreach (var avatar in avatars)
                Lot.ForwardCommand(new VMNetSimLeaveCmd()
                {
                    ActorUID = avatar.PersistID,
                    FromNet = false
                });

            //simulate for a bit to try get rid of the avatars on the lot
            try
            {
                for (int i = 0; i < 30 * TICKRATE && Lot.Entities.FirstOrDefault(x => x is VMAvatar && x.PersistID > 0) != null; i++)
                {
                    Lot.Tick();
                }
            }
            catch (Exception) { } //if something bad happens just immediately try to delete everyone

            avatars = new List<VMEntity>(Lot.Entities.Where(x => x is VMAvatar && (x.PersistID != 0 || (!(x as VMAvatar).IsPet))));
            foreach (var avatar in avatars) avatar.Delete(true, Lot.Context);
        }

        public (VM, DbLot)? AttemptLoad(int lotId)
        {
            DbLot LotPersist;

            using (var da = (SqlDA)DAFactory.Get())
            {
                var lot = da.Lots.Get(lotId);

                if (lot == null) return null;

                LotPersist = lot;
            }

            VM.UseWorld = false;
            var link = new VMTSOGlobalLinkStub();
            link.Database = new SimAntics.Engine.TSOGlobalLink.VMTSOStandaloneDatabase();
            var Lot = new VM(new VMContext(null), new VMServerDriver(link), new VMNullHeadlineProvider());
            Lot.Init();

            //first let's try load our adjacent lots.
            int attempts = 0;
            var lotStr = lotId.ToString("x8");
            var ringSize = Config.Services.Lots.First().RingBufferSize;

            while (++attempts < ringSize)
            {
                LOG.Info("Checking ring " + attempts + " for lot with dbid = " + lotId);
                try
                {
                    var path = Path.Combine(Config.SimNFS, "Lots/" + lotStr + "/state_" + LotPersist.ring_backup_num.ToString() + ".fsov");
                    using (var file = new BinaryReader(File.OpenRead(path)))
                    {
                        var marshal = new VMMarshal();
                        marshal.Deserialize(file);

                        // Don't bother using move flags to rotate.

                        Lot.Load(marshal);
                        CleanLot(Lot);
                        Lot.Reset();
                    }

                    using (var db = DAFactory.Get())
                        db.Lots.UpdateRingBackup(LotPersist.lot_id, LotPersist.ring_backup_num);

                    return (Lot, LotPersist);
                }
                catch (Exception e)
                {
                    LOG.Info("Ring load failed with exception: " + e.ToString() + " for lot with dbid = " + lotId);
                    LotPersist.ring_backup_num--;
                    if (LotPersist.ring_backup_num < 0) LotPersist.ring_backup_num += (sbyte)ringSize;
                }
            }

            LOG.Error("FAILED to load all backups for lot with dbid = " + lotId + "! Forcing lot close");
            var backupPath = Path.Combine(Config.SimNFS, "Lots/" + lotStr + "/failedRestore" + (DateTime.Now.ToBinary().ToString()) + "/");
            Directory.CreateDirectory(backupPath);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Config.SimNFS, "Lots/" + lotStr + "/")))
            {
                File.Copy(file, backupPath + Path.GetFileName(file));
            }

            return null;
        }

        private List<SignPluginJson> GetSignPluginData(List<DbObject> objs)
        {
            return objs.Select(obj => ReadSign(obj.object_id)).Where(x => x != null).ToList();
        }

        private List<CardPluginJson> GetDrawCardPluginData(List<DbObject> objs)
        {
            return objs.Select(obj => ReadCards(obj.object_id)).Where(x => x != null).ToList();
        }

        private VMAvatar CreateAvatar(VM vm)
        {
            return (VMAvatar)vm.Context.CreateObjectInstance(VMAvatar.TEMPLATE_PERSON, LotTilePos.OUT_OF_WORLD, Direction.NORTH).Objects[0];
        }

        private void ResetMotives(VMAvatar sim)
        {
            sim.SetMotiveData(VMMotive.Hunger, 100);
            sim.SetMotiveData(VMMotive.Comfort, 100);
            sim.SetMotiveData(VMMotive.Energy, 100);
            sim.SetMotiveData(VMMotive.Bladder, 100);
            sim.SetMotiveData(VMMotive.Hygiene, 100);
            sim.SetMotiveData(VMMotive.Fun, 100);
            sim.SetMotiveData(VMMotive.Social, 100);
        }

        private void ResetPosition(VM vm, VMAvatar sim)
        {
            var mailbox = vm.Entities.FirstOrDefault(x => (x.Object.OBJ.GUID == 0xEF121974 || x.Object.OBJ.GUID == 0x1D95C9B0));
            if (mailbox != null) VMFindLocationFor.FindLocationFor(sim, mailbox, vm.Context, VMPlaceRequestFlags.Default);
            else sim.SetPosition(LotTilePos.FromBigTile(3, 3, 1), Direction.NORTH, vm.Context);
        }

        private const int MAX_INTERACTION_ATTEMPT_COUNT = 30;
        private const int MAX_ROUTING_TICKS = 30 * 180; // 3 minutes

        private void EndInteraction(VM vm, VMAvatar ava, VMQueuedAction action)
        {
            vm.SendCommand(new VMNetInteractionCancelCmd()
            {
                ActorUID = ava.PersistID,
                ActionUID = action.UID,
            });

            for (int i = 0; i < MAX_INTERACTION_ATTEMPT_COUNT; i++)
            {
                // Wait for the interaction to end

                var newAction = ava.Thread.ActiveAction;
                if (newAction != action)
                {
                    return;
                }

                vm.Tick();
            }

            ava.Reset(vm.Context);
        }

        private bool TestSignRouteWithTeleporters(VM vm, VMAvatar ava, VMEntity sign, ref List<VMEntity> teleporterStarts)
        {
            if (TestSignRoute(vm, ava, sign))
            {
                return true;
            }

            if (teleporterStarts == null)
            {
                // Evaluate what teleporters are reachable from the mailbox

                var teleporters = vm.Context.ObjectQueries.GetObjectsByGUID(TELEPORTER_TYPE);

                teleporterStarts = new List<VMEntity>();

                foreach (var teleporter in teleporters)
                {

                }
            }

            // Can we get there from any of the teleporters?
            foreach (var start in teleporterStarts)
            {

            }

            return false;
        }

        private bool TestSignRoute(VM vm, VMAvatar ava, VMEntity sign)
        {
            // Place the avatar at the mailbox
            ResetMotives(ava);
            ResetPosition(vm, ava);

            // Interaction 2 is read.
            // for the card thing, interaction 2 is deck info

            vm.SendCommand(new VMNetInteractionCmd()
            {
                Interaction = 2,
                ActorUID = ava.PersistID,
                CalleeID = sign.ObjectID,
                Param0 = 0,
                Global = false
            });

            VMQueuedAction spyAction = null;

            for (int i = 0; i < MAX_INTERACTION_ATTEMPT_COUNT; i++)
            {
                // Wait for the interaction to show up.

                var action = ava.Thread.ActiveAction;
                if (action?.Callee == sign)
                {
                    spyAction = action;
                    break;
                }

                vm.Tick();

                if (i == MAX_INTERACTION_ATTEMPT_COUNT - 1)
                {
                    // Failed?
                    ava.Reset(vm.Context);
                    return false;
                }
            }

            // Wait for the action to either end (return false) or for the plugin to start (return true, forcibly end the interaction and wait)

            for (int i = 0; i < MAX_ROUTING_TICKS; i++)
            {
                // Has the plugin started?

                if (ava.Thread.EODConnection != null)
                {
                    EndInteraction(vm, ava, spyAction);
                    return true;
                }

                var action = ava.Thread.ActiveAction;
                if (action != spyAction)
                {
                    // The interaction ended
                    return false;
                }

                vm.Tick();
            }

            EndInteraction(vm, ava, spyAction);

            return false;
        }

        private HouseJson ProcessLot(int lotId, List<DbObject> allSigns, List<DbObject> allCards)
        {
            // Could be a bit faster by building a dictionary for this before each iteration, but not too important
            var mySigns = allSigns.Where(x => x.lot_id == lotId).ToList();
            var myCards = allCards.Where(x => x.lot_id == lotId).ToList();

            var signData = GetSignPluginData(mySigns);
            var cardData = GetDrawCardPluginData(myCards);

            if (signData.Count > 0 || cardData.Count > 0)
            {
                var lot = AttemptLoad(lotId);

                if (lot != null)
                {
                    var vm = lot.Value.Item1;
                    var dbLot = lot.Value.Item2;

                    // Create a dummy avatar to route to the destination, with visitor permissions.
                    var dummy = CreateAvatar(vm);
                    dummy.PersistID = 1;
                    vm.Context.ObjectQueries.RegisterAvatarPersist(dummy, dummy.PersistID);
                    vm.MyUID = 1;

                    bool admitGuests = AdmitModePublic(dbLot.admit_mode);

                    // Constructed if any route attempts fail. See ConstructTeleportStarts for more info.
                    List<VMEntity> teleporterStarts = null;

                    foreach (var sign in signData)
                    {
                        // Try to look up the sign on the lot.
                        var realSign = vm.GetObjectByPersist(sign.ObjectID);

                        if (realSign == null) continue;

                        // Can we route to it from the mailbox?
                        sign.IsReachable = TestSignRouteWithTeleporters(vm, dummy, realSign, ref teleporterStarts);
                        sign.Delete = !sign.IsReachable || !admitGuests;
                    }

                    foreach (var card in cardData)
                    {
                        // Try to look up the sign on the lot.
                        var realCard = vm.GetObjectByPersist(card.ObjectID);

                        if (realCard == null) continue;

                        // Can we route to it from the mailbox?
                        card.IsReachable = TestSignRouteWithTeleporters(vm, dummy, realCard, ref teleporterStarts);
                        card.Delete = !card.IsReachable || !admitGuests;
                    }

                    return new HouseJson()
                    {
                        HouseId = (uint)lotId,
                        HouseAdmitMode = dbLot.admit_mode,
                        HouseName = dbLot.name,
                        Signs = [.. signData],
                        Cards = [.. cardData]
                    };
                }

                return new HouseJson()
                {
                    HouseId = (uint)lotId,
                    HouseAdmitMode = 0,
                    HouseName = "(invalid)",
                    Signs = [.. signData],
                    Cards = [.. cardData]
                };
            }

            return null;
        }

        public int Run()
        {
            LOG.Info("Scanning content");
            VMContext.InitVMConfig(false);
            Content.Content.Init(Config.GameLocation, Content.ContentMode.SERVER);

            var publicHouses = new List<HouseJson>();
            var privateHouses = new List<HouseJson>();

            using (var da = (SqlDA)DAFactory.Get())
            {
                var allSigns = new List<DbObject>();
                foreach (uint guid in SignTypes)
                {
                    allSigns.AddRange(da.Objects.GetByType(guid));
                }

                var allDrawACard = da.Objects.GetByType(DRAW_A_CARD_TYPE);

                var allPluginObjects = new List<DbObject>(allSigns);
                allPluginObjects.AddRange(allDrawACard);

                var lots = GetUniqueLots(allPluginObjects);

                foreach (var lot in lots)
                {
                    var lotData = ProcessLot(lot, allSigns, allDrawACard);

                    if (lotData != null)
                    {
                        if (AdmitModePublic(lotData.HouseAdmitMode))
                            publicHouses.Add(lotData);
                        else
                            privateHouses.Add(lotData);
                    }
                }
            }

            var result = new ReviewJson()
            {
                PublicHouses = [.. publicHouses],
                PrivateHouses = [.. privateHouses]
            };

            var json = JsonConvert.SerializeObject(result, Formatting.Indented);

            File.WriteAllText("pluginReview.json", json);

            // - Find all objects with the interesting plugins, and their owner lots.
            //  - Without a lot, the plugin data should be lost.
            // - Load a lot from the list. Determine a spawn location for a sim at the mailbox.
            //  - Find the plugin object on the lot and load the plugin data.
            //  - If there's plugin data, try see if the object is reachable from the start position.
            //    - Signs can be read from a distance, cards must be accessed from the front.
            //    - Consider the use of teleporters (reachable tps should have their destinations as possible new starting locations), and some special doors that can be passed through (escape room)
            //  - Permission doors are special in that their data should always be cleared (but their effect on routing calculations remains)

            // The user then hand validates the list of plugins that were accepted/rejected automatically, then can modify the json to specifically allow/deny entries based on opinion.
            // This should be with respect to if the content should remain private or not. You can always feed back in the JSON without review.

            return 0;
        }

        private SignPluginJson ReadSign(uint objectPID)
        {
            var data = LoadPluginPersist(objectPID, SIGN_PLUGIN);

            if (data != null)
            {
                try
                {
                    var parsed = new VMEODSignsData(data);

                    return new SignPluginJson()
                    {
                        ObjectID = objectPID,
                        SignFlags = parsed.Flags,
                        Message = parsed.Text
                    };
                }
                catch (Exception ex)
                {
                    return null;
                }
            }

            return null;
        }

        private CardPluginJson ReadCards(uint objectPID)
        {
            var data = LoadPluginPersist(objectPID, DRAW_CARD_PLUGIN);

            if (data != null)
            {
                try
                {
                    var parsed = new VMEODGameCompDrawACardData(data);

                    return new CardPluginJson()
                    {
                        ObjectID = objectPID,
                        Title = parsed.GameTitle,
                        Description = parsed.GameDescription,
                        CardContents = [.. parsed.CardText],
                    };
                }
                catch (Exception ex)
                {
                    return null;
                }
            }

            return null;
        }

        private byte[] LoadPluginPersist(uint objectPID, uint pluginID)
        {
            if (objectPID == 0) return null;
            try
            {
                var objStr = objectPID.ToString("x8");
                var path = Path.Combine(Config.SimNFS, "Objects/" + objStr + "/Plugin/" + pluginID.ToString("x8") + ".dat");

                //if path does not exist, will throw FileNotFoundException
                using (var file = File.Open(path, FileMode.Open))
                {
                    var dat = new byte[file.Length];
                    file.ReadExactly(dat);
                    return dat;
                }
            }
            catch (Exception e)
            {
                //todo: specific types of exception that can be thrown here? instead of just catching em all
                if (!(e is FileNotFoundException))
                    //LOG.Error(e, 
                    Console.WriteLine("Failed to load plugin persist for object " + objectPID.ToString("x8") + " plugin " + pluginID.ToString("x8") + "!");
                return null;
            }
        }
    }
}
