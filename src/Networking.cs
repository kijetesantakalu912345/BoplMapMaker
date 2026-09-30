using Entwined;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using System.Reflection;
using Steamworks.Data;
using MonoMod.Utils;
//using UnityEngine.XR.Tango;

namespace MapMaker
{
    public static class NetworkingStuff
    {
        // OTHER MODS: you can check this field to see if map maker is currently trying to send a map (and thus getting close-ish to saturating the connection).
        // you can also attempt to do similar reflection shenanigans to access `PendingReliable` (aka `m_cbPendingReliable`), to see how much data
        // is currently in the unsent buffer, and SteamNetworkingUtils.SendBufferSize to see how large the maximum buffer size is (default is 512 kib).
        // map maker will leave some space free in the unsent buffer so that it's not completely saturated but it will use most of it.
        public static bool isMapBeingTransmitted = false;

        // WAIT MAKE SURE WE CAN'T MEMORY LEAK WITH PREVIOUS MAPS

        // each chunk can be up to 512 kib.
        // due to the entwined header and stuff the actual file size will be slightly smaller than 512 kib each but whatever.
        // wait now I'm not sure if the int packet length or the rest of the metadata from steamworks is included in the maximum or not...
        
        /// <summary>
        /// ((512 kib * 1024 kib/byte) - (8 bytes for the entwined header + 1 byte chunk number)) * 255 chunks = maximum map size of ~133.69 mb in online mode.
        /// I'll have to test it but this number may need to be divided by the amount of other connected players, in which case it'd be
        /// ~133.69 mb / 3 players = a maximum map size of ~44.5 mb in online mode.
        /// It looks like steamworks might have an unsent buffer size limit and a maximum message length of 512 kib, so that's where the division by up to 3
        /// other players in the lobby might need to come from. (Though the max map size would probably be fixed to 44.5 mb even in smaller lobbies).
        /// </summary>
        public const uint CHUNK_SIZE = (512 * 1024) - (8 + 1);
        public static List<byte> eachPlayerNextChunkNum = [0, 0, 0]; // using the order of `SteamManager.instance.connectedPlayers` LOOK AT hasReceivedLatestZip
        public static byte CurrentZipChunkNum = 0;
        public static List<byte> ReceivedZipBytes = new List<byte>();
        public static EntwinedPacketChannel<ZipMetadataPacket> ZipMetadataChannel;
        public static EntwinedPacketChannel<ZipChunkPacket> ZipChunkChannel;
        public static EntwinedPacketChannel<ZipArchivePacket> OldZipChannel;
        public static EntwinedPacketChannel<BetterStartRequestPacket> StartChannel;
        //used to know if the ZipArchivePacket was receved so that if it wasnt we can send it agien. this is the id of the zip that was received.
        //make sure everyone receives it.
        public static EntwinedPacketChannel<int> ZipReceivedChannel;
        public static Dictionary<SteamId, bool> hasReceivedLatestZip = new();
        public static float MillisecondsSinceLastChunkSend = 0;

        // cached reflected variables used to access `m_cbPendingReliable` (the amount of data in the unsent buffer for `Reliable` messages),
        // because it isn't publicly exposed in the >= 6 year old [1] version of facepunch steamworks that bopl uses, and I need to make sure
        // that I only try to send the next map chunk when the unsent buffer has enough space for it.
        // I'm caching these because when sending a map the mod is gonna constantly be checking m_cbPendingReliable and reflection is just slow in general.
        // [1]: https://github.com/Facepunch/Facepunch.Steamworks/commit/f887d8a9ba31a3f1dd0ceb65b48fbb3ad0881d90
        // maybe this wastes a little bit of memory but overall that's gonna be massively offset because now clients will only store the current map in ram,
        // instead of eventually constantly storing all of the host's maps.
        public static Type reflected_SteamNetworkingQuickConnectionStatus = null;
        public static object reflected_SteamNetworkingSockets_Internal_value = null;
        public static Delegate reflected_GetQuickConnectionStatus = null;

        public static ConstructorInfo QuickConnectionStatus_constructor = null;

        public static void Awake()
        {
            var newZipEntwiner = new GenericEntwiner<ZipChunkPacket>(ZipChunkPacketToByteArray, ByteArrayToZipChunkPacket);
            ZipChunkChannel = new EntwinedPacketChannel<ZipChunkPacket>(Plugin.instance, newZipEntwiner);
            ZipChunkChannel.OnMessage += OnZipChunk;
            var oldZipEntwiner = new GenericEntwiner<ZipArchivePacket>(ZipArchivePacketToByteArray, ByteArrayToZipArchivePacket);
            OldZipChannel = new EntwinedPacketChannel<ZipArchivePacket>(Plugin.instance, oldZipEntwiner);
            OldZipChannel.OnMessage += OnZipArchive;
            var startEntwiner = new GenericEntwiner<BetterStartRequestPacket>(BetterStartRequestPacketToByteArray, ByteArrayToBetterStartRequestPacket);
            StartChannel = new EntwinedPacketChannel<BetterStartRequestPacket>(Plugin.instance, startEntwiner);
            StartChannel.OnMessage += OnStartPacket;
            ZipReceivedChannel = new EntwinedPacketChannel<int>(Plugin.instance, new IntEntwiner());
            ZipReceivedChannel.OnMessage += OnZipReceivedConfirmed;
        }
        // I might be able to get away with doing this in `Awake()` but I don't want to risk it,
        // there's enough shenanigans going on during `Awake()` that I'd prefer to just wait until `Start()` to do this stuff.
        public static void Start()
        {
            // this would work and I'd be able to use dynamic invoke, but apparently it also has overhead.
            // so there's another approach I want to try.
            // I'll have this commented as a backup.
            // (apparently potentially even more overhead than just `Invoke()`ing the function with reflection?)
            // Assembly steamworksAssembly = typeof(SteamNetworkingSockets).Assembly;
            // reflected_SteamNetworkingQuickConnectionStatus = steamworksAssembly.GetType("SteamNetworkingQuickConnectionStatus").MakeGenericType();
            // var typeOfAction = typeof(Action<,>).MakeGenericType(typeof(Connection), reflected_SteamNetworkingQuickConnectionStatus);
            // var reflected_SteamNetworkingSockets_Internal = typeof(SteamNetworkingSockets).GetField("Internal", BindingFlags.NonPublic);
            // reflected_SteamNetworkingSockets_Internal_value = reflected_SteamNetworkingSockets_Internal.GetValue(null);
            // MethodInfo reflected_GetQuickConnectionStatus_methodInfo = reflected_SteamNetworkingSockets_Internal.GetType()
            //     .GetMethod("GetQuickConnectionStatus", BindingFlags.NonPublic);
            // reflected_GetQuickConnectionStatus = reflected_GetQuickConnectionStatus_methodInfo.CreateDelegate(
            //     typeOfAction, reflected_GetQuickConnectionStatus_methodInfo);
            //reflected_GetQuickConnectionStatus.DynamicInvoke(connection instance, SteamNetworkingQuickConnectionStatus instance);

            // honestly maybe I should just use a publicizer on the facepunch steamworks DLL and directly call the things I need...
            
            // QuickConnectionStatus_constructor = reflected_SteamNetworkingQuickConnectionStatus.GetConstructor(
            //     BindingFlags.NonPublic,
            //     null,
            //     [typeof(Connection), reflected_SteamNetworkingQuickConnectionStatus],
            //     null
            // );
        }
        public static void Update()
        {
            var delta = (Time.deltaTime * 1000f);
            MillisecondsSinceLastChunkSend += delta;
            // SOME MORE CONDITIONS NEED TO BE ADDED HERE
            if(isMapBeingTransmitted && hasReceivedLatestZip.Count < SteamManager.instance.connectedPlayers.Count && SteamManager.LocalPlayerIsLobbyOwner
                && MillisecondsSinceLastChunkSend > 16) // make sure we're not completely hammering the CPU (PROBABLY UNNECESSARY? CHECK THIS)
            {
                // this bool is probably false if the connection handle is invalid but I'm not sure.
                // target: `internal bool GetQuickConnectionStatus(Connection hConn, ref SteamNetworkingQuickConnectionStatus pStats);`
                // we also need to access `internal struct SteamNetworkingQuickConnectionStatus` and pass it by reference to the above function.
                // hmm...
                // ChatGPT (yeah boo AI usage I know, but I just wanted to ask specifically how slow using `Invoke()` is compared to a normal call
                // once you already have the initial reflection cached and google wasn't returning the results I wanted for my very specific question,
                // I'll figure out how to implement its base suggestion on my own instead of copy pasting its answer directly).
                // says that creating a delegate would be faster than just using `MethodInfo.Invoke()`/`ConstructorInfo.Invoke()` directly.
                // technically we can probably get away with it fine but ehhh...
                // ALSO WAIT IT'S PROBABLY FASTER TO JUST MAKE ONE INSTANCE OF THE CONSTRUCTOR WHEN WE START TRANSMITTING A MAP INSTEAD OF CREATING IT
                // FRESH POTENTIALLY THOUSANDS OF TIMES.
                // bool returnedBool = reflected_GetQuickConnectionStatus.Invoke(
                //     reflected_SteamNetworkingSockets_Internal_value,
                //     [CONNECTION_INSTANCE, INSTANCE_OF_reflected_steamNetworkingQuickConnectionStatus]
                // );


                MillisecondsSinceLastChunkSend = 0;
            }


            // OLD STUFF
            // I ... don't think this is necessary?
            // basically via a small bit of code analysis it's not very hard to see that entwined ends up using `SendType.Reliable`
            // and from the facepunch steamworks documentation I'm able to find on it, it seems like that should just be a reliable way to send messages.
            // (later)
            // https://partner.steamgames.com/doc/api/steamnetworkingtypes#message_sending_flags
            // follow that link and look at the comment above `int m_cbPendingUnreliable` and `int m_cbPendingReliable`.
            // with that comment and from how else `SendType.Reliable` is described, I think reliable data only gets dropped if a disconnect happens.
            // var delta = (Time.deltaTime * 1000f);
            // MillisecondsSinceLastZipSend = MillisecondsSinceLastZipSend + delta;
            // if (MillisecondsSinceLastZipSend > MilisecondsToDelayBeforeResendingZip && hasReceivedLatestZip.Count < SteamManager.instance.connectedPlayers.Count
            //     && SteamManager.LocalPlayerIsLobbyOwner)
            // {
            //     // resend the zip
            //     MillisecondsSinceLastZipSend = 0;
            //     ZipArchivePacket zipArchivePacket = new ZipArchivePacket
            //     {
            //         zip = Plugin.MyZipArchives[Plugin.NextMapIndex],
            //         amountOfZips = Plugin.MyZipArchives.Length,
            //         id = Plugin.NextMapIndex
            //     };
            //     OldZipChannel.SendMessage(zipArchivePacket);
            //     //set all this stuff
            //     MilisecondsToDelayBeforeResendingZip = NetworkingStuff.GetDelayForResendingZip();
            //     UnityEngine.Debug.LogWarning("map file appears to have been droped, resending");
            // }
        }

        // maybe I can make this easier by calling this on round end instead of HostGame()/HostNextLevel()/etc?
        // then just have those functions not be able to get called or have them return immediately if the map hasn't finished sending.
        // that'd take a bit more analysis of the game's code but it'd be a lot cleaner than the messy delegate callback solution im thinking of.
        // sending the map when the round actually ends is also just a better time to send it instead of only sending it when the game tries
        // to start the next round.
        // I was gonna say that'd require a transpiler patch on CharacterSelectHandler_online.Update() and it does if I wanted it to be perfectly clean,
        // but I could probably also just use a prefix on CharacterSelectHandler_online.TryStartGame().
        // actually I don't think it'd even be that hard to just do the transpiler anyway.
        public static void StartSendingMapInChunks()
        {
            // sending the start request packet is gonna have to be moved to a callback or something.
            // making this use the update loop would probably be a good and relatively easy to implement idea.
        }

        public static byte[] ZipChunkPacketToByteArray(ZipChunkPacket zipChunkPacket)
        {
            byte[] packetBytes = new byte[1 + zipChunkPacket.zipChunkBytes.Length];
            packetBytes[0] = zipChunkPacket.chunkNum;
            zipChunkPacket.zipChunkBytes.CopyTo(packetBytes, index: 1);
            return packetBytes;
        }

        public static ZipChunkPacket ByteArrayToZipChunkPacket(byte[] packetBytes)
        {
            ZipChunkPacket zipChunkPacket = new ZipChunkPacket();
            zipChunkPacket.chunkNum = packetBytes[0];
            zipChunkPacket.zipChunkBytes = new byte[packetBytes.Length - 1];
            Array.Copy(packetBytes, 1, zipChunkPacket.zipChunkBytes, 0, packetBytes.Length - 1);
            return zipChunkPacket;
        }

        public static void OnZipChunk(ZipChunkPacket zipChunkPacket, PacketSourceInfo sourceInfo)
        {
            Plugin.logger.LogInfo("received chunk " + zipChunkPacket.chunkNum);
            // maybe move some of this edge case handling into the code that handles receiving the zip chunks.
            if(CurrentZipChunkNum > zipChunkPacket.chunkNum)
            {
                // outside of testing this message shouldn't generally be possible with the SendMessageTo() function I (ki) added to entwined.
                // ok to be fair it could come up if the map chunk acknowledgement got dropped.
                Plugin.logger.LogWarning("already received chunk " + zipChunkPacket.chunkNum + " earlier, ignoring...");
                return;
            }
            // TODO: think about this if statement.
            if(CurrentZipChunkNum != zipChunkPacket.chunkNum)
            {
                throw new NotImplementedException();
            }
            // UNFINISHED
        }

        public static byte[] ZipArchivePacketToByteArray(ZipArchivePacket archive)
        {
            //chatgpt code
            using (var memoryStream = new MemoryStream())
            {
                // Create a new ZipArchive in the MemoryStream
                using (var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                {
                    foreach (var entry in archive.zip.Entries)
                    {
                        var newEntry = zipArchive.CreateEntry(entry.FullName);
                        using (var entryStream = entry.Open())
                        using (var newEntryStream = newEntry.Open())
                        {
                            entryStream.CopyTo(newEntryStream);
                        }
                    }
                }
                //my code
                var bytes = memoryStream.ToArray().ToList();
                bytes.AddRange(BitConverter.GetBytes(Plugin.zipArchives.Length));
                bytes.AddRange(BitConverter.GetBytes(archive.id));
                return bytes.ToArray();
            }
        }
        public static ZipArchivePacket ByteArrayToZipArchivePacket(byte[] byteArray)
        {
            List<byte> bytes = byteArray.ToList();
            bytes.RemoveRange(bytes.Count - 8, 8);
            byte[] data = bytes.ToArray();
            var memoryStream = new MemoryStream(data);
            //https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/types/how-to-convert-a-byte-array-to-an-int
            // If the system architecture is little-endian (that is, little end first),
            // reverse the byte array.
            byte[] bytes2 = { byteArray[byteArray.Length - 8], byteArray[byteArray.Length - 7], byteArray[byteArray.Length - 6], byteArray[byteArray.Length - 5] };
            byte[] bytes3 = { byteArray[byteArray.Length - 4], byteArray[byteArray.Length - 3], byteArray[byteArray.Length - 2], byteArray[byteArray.Length - 1] };
            //idk why but the microsoft exsample is backwords?
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes2);
                Array.Reverse(bytes3);
            }
            var zipPacket = new ZipArchivePacket
            {
                zip = new ZipArchive(memoryStream, ZipArchiveMode.Read),

                amountOfZips = BitConverter.ToInt32(bytes2, 0),
                id = BitConverter.ToInt32(bytes3, 0)
            };
            return zipPacket;
        }
        public static void OnZipArchive(ZipArchivePacket payload, PacketSourceInfo sourceInfo)
        {
            //if its the host we add them to the list of zip archives and do the rest of the initalison for them.
            if (SteamManager.instance.currentLobby.IsOwnedBy(sourceInfo.Identity.SteamId))
            {
                UnityEngine.Debug.Log($"length of zip array is {payload.amountOfZips}");
                UnityEngine.Debug.Log($"id is {payload.id}");
                if (Plugin.zipArchives.Length < payload.amountOfZips)
                {
                    Plugin.zipArchives = new ZipArchive[payload.amountOfZips];
                    Plugin.MapJsons = new string[payload.amountOfZips];
                    Plugin.MetaDataJsons = new string[payload.amountOfZips];
                }
                Plugin.zipArchives[payload.id] = payload.zip;
                Plugin.MapJsons[payload.id] = Plugin.GetFileFromZipArchive(payload.zip, Plugin.IsBoplMap)[0];
                Plugin.MetaDataJsons[payload.id] = Plugin.GetFileFromZipArchive(payload.zip, Plugin.IsMetaDataFile)[0];
                //confirm we got the packet
                ZipReceivedChannel.SendMessage(payload.id);
            }
        }

        public static byte[] BetterStartRequestPacketToByteArray(BetterStartRequestPacket BetterStartRequest)
        {
            StartRequestPacket startRequest = BetterStartRequest.startRequest;
            byte[] buff = new byte[105];
            NetworkTools.EncodeStartRequest(ref buff, startRequest);
            List<byte> packet = buff.ToList();
            packet.AddRange(BitConverter.GetBytes(BetterStartRequest.MapIndex));
            packet.Add(BetterStartRequest.MapIdForInputStuff);
            return packet.ToArray();
        }
        public static BetterStartRequestPacket ByteArrayToBetterStartRequestPacket(byte[] bytes)
        {
            List<byte> bytes2 = bytes.ToList();
            bytes2.RemoveRange(bytes2.Count - 4, 4);
            var uintConversionArray = new byte[4];
            var ulongConversionArray = new byte[8];
            var ushortConversionArray = new byte[8];
            StartRequestPacket startRequest = NetworkTools.ReadStartRequest(bytes2.ToArray(), ref uintConversionArray, ref ulongConversionArray, ref ushortConversionArray);
            byte[] bytes3 = { bytes[bytes.Length - 5], bytes[bytes.Length - 4], bytes[bytes.Length - 3], bytes[bytes.Length - 2] };
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes3);
            }
            var MapIndex = BitConverter.ToInt32(bytes3, 0);
            var MapIdForInputStuff = bytes[bytes.Length - 1];
            UnityEngine.Debug.Log($"MapIndex is {MapIndex}");
            var betterStartRequestPacket = new BetterStartRequestPacket
            {
                startRequest = startRequest,
                MapIndex = MapIndex,
                MapIdForInputStuff = MapIdForInputStuff
            };
            return betterStartRequestPacket;
        }
        public static void OnStartPacket(BetterStartRequestPacket payload, PacketSourceInfo sourceInfo)
        {
            if (SteamManager.instance.currentLobby.IsOwnedBy(sourceInfo.Identity.SteamId))
            {
                SteamManager.startParameters = payload.startRequest;
                // this was useful with the old system for sending maps where maps were never unloaded online, but now maps are always unloaded online.
                //Plugin.CurrentMapIndex = payload.MapIndex;
                Plugin.CurrentMapIndex = 0;

                Plugin.CurrentLevelIdForInputsOnlineThingy = payload.MapIdForInputStuff;
                //UnityEngine.Debug.Log($"receved start requst packet. set map id for input stuff to {Plugin.CurrentLevelIdForInputsOnlineThingy}");
                
                //its max exsclusive min inclusinve
                if (Plugin.MapJsons.Length != 0)
                {
                    UnityEngine.Debug.Log($"we have {Plugin.MapJsons.Length} maps");
                    UnityEngine.Debug.Log($"the host sent us map index {payload.MapIndex} (should be map {Plugin.CurrentMapIndex} in our array)");
                    Dictionary<string, object> MetaData = MiniJSON.Json.Deserialize(Plugin.MetaDataJsons[Plugin.CurrentMapIndex]) as Dictionary<string, object>;
                    var type = Convert.ToString(MetaData["MapType"]);
                    UnityEngine.Debug.Log("getting map type");
                    switch (type)
                    {
                        case "space":
                            GameSession.currentLevel = (byte)Plugin.SpaceMapId;
                            SteamManager.startParameters.currentLevel = GameSession.currentLevel;
                            break;
                        case "snow":
                            GameSession.currentLevel = (byte)Plugin.SnowMapId;
                            SteamManager.startParameters.currentLevel = GameSession.currentLevel;
                            break;
                        default:
                            GameSession.currentLevel = (byte)Plugin.GrassMapId;
                            SteamManager.startParameters.currentLevel = GameSession.currentLevel;
                            break;
                    }
                    Plugin.CurrentMapIntID = Convert.ToInt32(MetaData["MapUUID"]); // map IDs are not actually UUIDs but it's too late to rename the json property
                }
                if (SteamManager.instance.currentLobby.IsOwnedBy(sourceInfo.Identity.SteamId))
                {
                    SteamManager.instance.EncodeCurrentStartParameters_forReplay(ref SteamManager.instance.networkClient.EncodedStartRequest, SteamManager.startParameters, false);
                    UnityEngine.Debug.Log(string.Concat(new object[]
                    {
                "FORCESTARTGAME: connectedPlayerCount = ",
                SteamManager.instance.connectedPlayers.Count,
                " packet from ",
                sourceInfo.Identity.SteamId
                    }));
                    if (GameSession.inMenus)
                    {
                        CharacterSelectHandler_online.ForceStartGame(null);
                        return;
                    }
                    SteamManager.ForceLoadNextLevel();
                    return;
                }
            }
        }
        public static void OnZipReceivedConfirmed(int payload, PacketSourceInfo sourceInfo)
        {
            if (payload == Plugin.NextMapIndex)
            {
                hasReceivedLatestZip[sourceInfo.SenderSteamId] = true;
            }
        }
        // public static int GetDelayForResendingZip()
        // {
        //     float maxPing = 0;
        //     foreach (var connection in SteamManager.instance.connectedPlayers)
        //     {
        //         if (connection.ping > maxPing)
        //         {
        //             maxPing = connection.ping;
        //         }
        //     }
        //     UnityEngine.Debug.Log(maxPing);
        //     //the ping in milliseconds + 250 miliseconds of leway
        //     return (int)(maxPing * 1000f) + 250;
        // }
    }
    public struct ZipMetadataPacket
    {
        public uint amountOfChunks;
    }
    public struct ZipChunkPacket
    {
        public byte chunkNum;
        public byte[] zipChunkBytes;
    }
    public struct ZipArchivePacket
    {
        public ZipArchive zip;
        public int amountOfZips;
        public int id;
    }
    public struct BetterStartRequestPacket
    {
        public StartRequestPacket startRequest;
        public int MapIndex; // this gets ignored with the new map sending system that always unloads old maps.
        public byte MapIdForInputStuff;
    }
}
