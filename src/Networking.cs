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
using UnityEngine.Diagnostics;
using Unity.Mathematics;
using UnityEngine.AI;
using System.IO.Hashing;
//using UnityEngine.XR.Tango;

namespace MapMaker
{
    public static class NetworkingStuff
    {
        // OTHER MODS: you can check this field to see if map loader is currently trying to send a map (and thus possibly getting close-ish
        // to saturating the connection).
        // map loader will leave some space free in the unsent buffer so that it's not completely saturated but it will be able to use a lot of it.
        // (look at the below 'MAX_PACKET_SIZE' and 'MAX_PREFERED_PACKET_SIZE' to see exactly how much. though other map loader packets may
        // make the real total network usage ever so slightly higher.)
        // you can also publicize facepunch steamworks to access `cbPendingReliable`, to see how much data is currently in the unsent buffer,
        // and `SteamNetworkingUtils.SendBufferSize` to see how large the maximum buffer size is (default is 512 kib).
        // (you can access that information by calling `SteamNetworkingSockets.Internal.GetQuickConnectionStatus()`, which gives it to you in a ref struct)
        public static bool IsMapBeingTransmitted = false;
        //public static bool sendStartReqRightAfterMapSends = false;
        public static bool SendStartReqAndForceLoadNextLevelAfterMapSends = false;
        public static bool IsFirstMap = true;
        public static bool ForceDoNotAttemptAnotherStartUntilMenuAwayFlag = false;
        //public static bool Host_DisableSendingAnotherStartReqWhileOnMenu = true;
        // // used by the host to queue up a start request and send it after the map finishes sending if we receive a start suggest before the map has been sent.
        //public static bool hasBetterStartReqPacketBeenPrepared = false;
        //public static bool hasStartReqPacketBeenSent = false;
        // nah we'll just create it right when we need it.
        //public static BetterStartRequestPacket preparedBetterStartRequestPacket; // note that we'll also have to set SteamManager.startParameters.
        public const uint HEADER_SIZE = 8 + 1; // 8 byte entwined header + 1 byte chunk number in our packet.
        // MAX_PACKET_SIZE = 480 kib leaves (512-480) = 32 kib left in the buffer for the vanilla game and other mods in the worst case,
        // and gives a max map size of ((480 * 1024) - 9) * 256 = 119.9978027344 mebibytes = 125.82681600002621281 megabytes.
        public const uint MAX_PACKET_SIZE = 480 * 1024;
        // MAX_PREFERED_PACKET_SIZE = 64 kib leaves 256 kib available if we send a chunk to all 4 players at the same time.
        public const uint MAX_PREFERED_PACKET_SIZE = 64 * 1024;
        public const uint MAX_FILE_SIZE = (MAX_PACKET_SIZE - HEADER_SIZE) * 256;
        public const uint MAX_PREFERED_FILE_SIZE = MAX_PREFERED_PACKET_SIZE - HEADER_SIZE;
        public static Dictionary<SteamId, short>  Host_EachPlayerNextChunkNum = new();
        public static Dictionary<SteamId, short>  Host_EachPlayerLastAckedChunkNum = new();
        // once every player in the lobby has acked the last start request, we'll allow new ones to be sent.
        // if it hasn't been acked by everybody the host just, won't send another start request.
        public static Dictionary<SteamId, byte> Host_EachPlayerLastAckedStartReq = new();
        public static Dictionary<SteamId, bool> OLD_I_THINK_hasFullyReceivedLatestZip = new();
        // this might seem overkill but this will be quite useful if we have to send maps that are larger than MAX_PREFERED_PACKET_SIZE.
        // it's a lot easier to just calculate what all of the packet sizes will need to be once ahead of time before we start sending.
        //public static List<uint> Host_ChunkSizes = [];
        public static uint Host_LargeChunkSize = 0;
        public static uint Host_SmallChunkSize = 0;
        // a new map will always have at least 1 large chunk so Host_LargeChunkAmountMinus1 = 0 actually means Host_LargeChunkAmount = 1
        public static uint Host_LargeChunkAmountMinus1 = 0;
        public static uint Host_SmallChunkAmount = 0;
        public static byte Client_CurrentChunkNum = 0;
        public static List<byte> NextMapByteChunks = new List<byte>();
        public static byte NextLevelIdForOnlineInputs = 0;
        public static EntwinedPacketChannel<byte> StartRequestAckChannel;
        public static EntwinedPacketChannel<byte> ChunkIDAckChannel;
        // I was tempted to also use `ChunkIDAckChannel` for the purposes of `NewMapSendingStatusChannel`,
        // but doing so just makes things messier and harder to understand. it also creates a rather long variable name.
        public static EntwinedPacketChannel<byte> NewMapSendingStatusChannel;
        public static EntwinedPacketChannel<ZipChunkPacket> ZipChunkChannel;
        public static EntwinedPacketChannel<ZipArchivePacket> OldZipChannel;
        public static EntwinedPacketChannel<BetterStartRequestPacket> StartChannel;
        //used to know if the ZipArchivePacket was received so that if it wasnt we can send it agien. this is the id of the zip that was received.
        //make sure everyone receives it.
        // (note from later: that isn't necessary, steamworks will handle resending maps for you if they get dropped.)
        public static EntwinedPacketChannel<int> OldZipReceivedChannel;
        public static uint LAST_nextChunkSize = 0;
        //public static bool Host_Flag_BlockSendingExtraStartRequestByHoldingNextLevelButton = false;
        private static bool AllChunksAcked = false;
        public static void Awake()
        {
            var newZipEntwiner = new GenericEntwiner<ZipChunkPacket>(ZipChunkPacketToByteArray, ByteArrayToZipChunkPacket);
            ZipChunkChannel = new EntwinedPacketChannel<ZipChunkPacket>(Plugin.instance, newZipEntwiner);
            ZipChunkChannel.OnMessage += OnZipChunk;
            var startEntwiner = new GenericEntwiner<BetterStartRequestPacket>(BetterStartRequestPacketToByteArray, ByteArrayToBetterStartRequestPacket);
            StartChannel = new EntwinedPacketChannel<BetterStartRequestPacket>(Plugin.instance, startEntwiner);
            StartChannel.OnMessage += OnStartPacket;
            var singleByteEntwiner = new GenericEntwiner<byte>(SingleByteToByteArray, ByteArrayToSingleByte);
            ChunkIDAckChannel = new EntwinedPacketChannel<byte>(Plugin.instance, singleByteEntwiner);
            ChunkIDAckChannel.OnMessage += OnChunkAck;
            NewMapSendingStatusChannel = new EntwinedPacketChannel<byte>(Plugin.instance, singleByteEntwiner);
            NewMapSendingStatusChannel.OnMessage += OnNewMapSendingStatus;
            StartRequestAckChannel = new EntwinedPacketChannel<byte>(Plugin.instance, singleByteEntwiner);
            StartRequestAckChannel.OnMessage += OnStartRequestAck;

            var oldZipEntwiner = new GenericEntwiner<ZipArchivePacket>(ZipArchivePacketToByteArray, ByteArrayToZipArchivePacket);
            OldZipChannel = new EntwinedPacketChannel<ZipArchivePacket>(Plugin.instance, oldZipEntwiner);
            OldZipChannel.OnMessage += OnZipArchive;
            OldZipReceivedChannel = new EntwinedPacketChannel<int>(Plugin.instance, new IntEntwiner());
            OldZipReceivedChannel.OnMessage += OnZipReceivedConfirmed;
        }
        public static void Update()
        {
            // SOME MORE CONDITIONS NEED TO BE ADDED HERE
            // also wait this would attempt to proceed if somebody fully received the map and disconnected but 1 player still in the lobby hasn't
            // fully received the map yet...

            // the transfer speed might get sorta limited from the update rate but it's also only a few extra seconds maybe.
            // google says that (500 kib) / ((1 / 60) * seconds) = 30.72 MBps, and in online games the target framerate is set to 60.
            // in the main menus the target framerate is 300 so maps might send faster before the game starts but whatever it's good enough.

            // (ack = acknowledgement)
            // when every player has acked the final map chunk, we handle sending the start request packet in the map chunk ack handling code.
            if(IsMapBeingTransmitted && SteamManager.LocalPlayerIsLobbyOwner)
            {
                AllChunksAcked = true; // this will get set to false later in the loop unless the condition is actually supposed to be true.

                // `PendingReliable` (`m_cbPendingReliable`) was publicly exposed to c# in facepunch steamworks *>= 6 years ago* [1],
                // but bopl battle is somehow using a version older than that, so to access it I've had to publicize the DLL
                // so that I can access the `internal` members that it hides behind in this version.
                // (`m_cbPendingReliable` is the amount of bytes that are currently in the (reliable) unsent buffer, which has a maximum size,
                // so I have to use `m_cbPendingReliable` to tell when I have enough space in the buffer to send the next map chunk).
                // I could also do it with reflection but that's a lot more work and would come with extra overhead, even if I did some shenaniagns
                // to make the overhead only happen once when the mod is starting.
                // target: `internal bool GetQuickConnectionStatus(Connection hConn, ref SteamNetworkingQuickConnectionStatus pStats);`
                // we also need to access `internal struct SteamNetworkingQuickConnectionStatus` and pass it by reference to the above function.
                // [1]: https://github.com/Facepunch/Facepunch.Steamworks/commit/f887d8a9ba31a3f1dd0ceb65b48fbb3ad0881d90
                foreach (SteamConnection player in SteamManager.instance.connectedPlayers)
                {
                    short nextChunkNum = Host_EachPlayerNextChunkNum[player.id];

                    // SteamNetworkingQuickConnectionStatus quickStatus = new();
                    // SteamNetworkingSockets.Internal.GetQuickConnectionStatus(player.Connection, ref quickStatus);
                    //Plugin.logger.LogInfo(//"buffer space: " + (SteamNetworkingUtils.SendBufferSize - quickStatus.CbPendingReliable)
                    /*+ " could send prev: " + (SteamNetworkingUtils.SendBufferSize - quickStatus.CbPendingReliable >= LAST_nextChunkSize)
                    + */
                    // " out b/s: " + quickStatus.OutBytesPerSec + " in b/s: " + quickStatus.InBytesPerSec
                    // + " send rate b/s: " + quickStatus.SendRateBytesPerSecond + " queue time: " + quickStatus.EcQueueTime
                    // + " loc: " + quickStatus.ConnectionQualityLocal + " rem: " + quickStatus.ConnectionQualityRemote
                    // + " buffer usage :" + (((double)quickStatus.CbPendingReliable / (double)SteamNetworkingUtils.SendBufferSize) * 100) + "% ");

                    if (Host_EachPlayerLastAckedChunkNum[player.id] >= nextChunkNum - 1) // hopefully this doesn't byte underflow.
                    {
                        uint nextChunkSize = Host_LargeChunkSize;
                        if (nextChunkNum > Host_LargeChunkAmountMinus1)
                        {
                            nextChunkSize = Host_SmallChunkSize;
                        }
                        LAST_nextChunkSize = nextChunkSize;
                        //Plugin.logger.LogInfo("nextChunkNum: " + nextChunkNum + ", Host_LargeChunkAmountMinus1: " + Host_LargeChunkAmountMinus1
                        //+ " nextChunkSize == Host_SmallChunkSize: " + (nextChunkSize == Host_SmallChunkSize));
                        SteamNetworkingQuickConnectionStatus quickStatus = new();
                        SteamNetworkingSockets.Internal.GetQuickConnectionStatus(player.Connection, ref quickStatus);
                        //Plugin.logger.LogInfo("SteamNetworkingUtils.SendBufferSize: " + SteamNetworkingUtils.SendBufferSize + " quickStatus.CbPendingReliable: "
                        //+ quickStatus.CbPendingReliable + " SteamNetworkingUtils.SendBufferSize - quickStatus.CbPendingReliable: "
                        //+ (SteamNetworkingUtils.SendBufferSize - quickStatus.CbPendingReliable) + " nextChunkSize: " + nextChunkSize);
                        // Plugin.logger.LogInfo("SteamNetworkingUtils.SendBufferSize - quickStatus.CbPendingReliable: "
                        // + (SteamNetworkingUtils.SendBufferSize - quickStatus.CbPendingReliable) + ", nextChunkSize: " + nextChunkSize);
                        if (SteamNetworkingUtils.SendBufferSize - quickStatus.CbPendingReliable >= nextChunkSize)
                        {
                            Plugin.logger.LogInfo("sending chunk " + nextChunkNum);
                            uint startIndex = (uint)(math.min(nextChunkNum, Host_LargeChunkAmountMinus1 + 1) * Host_LargeChunkSize)
                                            + (uint)(math.max(0, (int)nextChunkNum - (int)(Host_LargeChunkAmountMinus1 + 1)) * Host_SmallChunkSize);
                            if (nextChunkNum == 0)
                            {
                                startIndex = 0;
                            }

                            byte[] nextChunkBytes = new byte[nextChunkSize];
                            Plugin.logger.LogInfo("nextChunkSize: " + nextChunkSize + ", range = (" + startIndex + ", " + (startIndex + (nextChunkSize)) + ") Host_LargeChunkSize: " + Host_LargeChunkSize);
                            //Plugin.logger.LogInfo("size of NextMapByteChunks: " + NextMapByteChunks.Count);
                            NextMapByteChunks.GetRange((int)startIndex, (int)(nextChunkSize)).CopyTo(nextChunkBytes);
                            var zipChunkPacket = new ZipChunkPacket()
                            {
                                chunkNum = (byte)Host_EachPlayerNextChunkNum[player.id],
                                zipChunkBytes = nextChunkBytes
                            };
                            ZipChunkChannel.SendMessageTo(zipChunkPacket, player.Connection);
                            Host_EachPlayerNextChunkNum[player.id] += 1;
                        }
                    }
                    // ^ chunk sending checks are done.
                    // now we just need to check if every chunk has been acked yet or not.
                    // (this was moved out of OnChunkAck() to fix potential edge cases)
                    if (Host_EachPlayerLastAckedChunkNum[player.id] < Host_LargeChunkAmountMinus1 + Host_SmallChunkAmount)
                    {
                        AllChunksAcked = false;
                    }
                }
                if (AllChunksAcked)
                {
                    IsMapBeingTransmitted = false;
                    NewMapSendingStatusChannel.SendMessage(0);
                    if (SendStartReqAndForceLoadNextLevelAfterMapSends || IsFirstMap)
                    {
                        Plugin.logger.LogInfo("sending start request immediately.");
                        SendBetterStartRequestPacket();
                    }
                    else
                    {
                        Plugin.logger.LogInfo("not sending start request immediately.");
                    }
                }
            }
        }

        // maybe I can make this easier by calling this on round end instead of HostGame()/HostNextLevel()/etc?
        // then just have those functions not be able to get called or have them return immediately if the map hasn't finished sending.
        // that'd take a bit more analysis of the game's code but it'd be a lot cleaner than the messy delegate callback solution im thinking of.
        // sending the map when the round actually ends is also just a better time to send it instead of only sending it when the game tries
        // to start the next round.
        // I was gonna say that'd require a transpiler patch on CharacterSelectHandler_online.Update() and it does if I wanted it to be perfectly clean,
        // but I could probably also just use a prefix on CharacterSelectHandler_online.TryStartGame().
        // actually I don't think it'd even be that hard to just do the transpiler anyway.
        // ok but it's just easier to make the map send when the host presses the start button.

        // wait yeah no I'm gonna need transpilers for this, I only want the next round to start when a round start is suggested, not as soon as the round ends,
        // and I need to send the map first before allowing start requests (or at least I don't want to make this more complicated by allowing that).
        // hmmm...
        // actually no, easier solution that doesn't require any transpilers: just make the host not send the start request packet until the map finishes sending.
        // if the host receives a start suggest before that point, the mod will keep a note to send the start request right after the map but it will wait
        // to finish sending the map before it sends the start request.
        // in theory that should work fine? I'll just have to make sure it doesn't break with edge cases like a player disconnecting mid map send
        // after the host made a note to queue up a start request as soon as the map finished sending.
        // probably also add text to the menu that shows how many chunks the local player has received.
        // (for the host probably show how many chunks have been acknowledged by everyone).

        // OK TODO FOR THIS FUNCTION: call this in a patch to prepareNextlevel()
        // if a desync occurs and an attempt to go to the next level is made,
        // we'll re-send the previous start request again, matching up with the normal behaviour.
        // if prepareNextlevel() gets called (and meaning that `DeclareGameUndecided()` or `WinGame()` got called), we'll prep the new map.
        // ok I'll probably actually have it get called from `GameSession.RandomBagLevel()` because there's already a patch on that function
        // (so compatability with other mods is less likely to be affected) and it probably makes slightly more sense to do it there.
        // prepareNextlevel() is basically just a wrapper around `GameSession.RandomBagLevel()` anyway.
        public static void StartSendingMapInChunks()
        {
            // potentially re-roll for a higher max send rate.
            int i_retryForBetterSendRateMax = 0;
            while (i_retryForBetterSendRateMax < 10 && Steamworks.SteamNetworkingUtils.GetConfigInt(Steamworks.NetConfig.SendRateMax) < 1_000_000)
            {
                Steamworks.SteamNetworkingUtils.SetConfigInt(Steamworks.NetConfig.SendRateMax, 268435456);
            }
            if (Plugin.numOfMapsUsableOnline == 0)
            {
                Plugin.logger.LogError("You can't host this match because you have no maps that are usable online.");
                GameSessionHandler.LeaveGame(abandonLobbyEntirely: true, sendOthersBackToAbilitySelect: true);
                return;
            }
            Plugin.NextMapIndex = Plugin.RandomBagLevel();
            while (!Plugin.MapsUsableOnline[Plugin.NextMapIndex])
            {
                Plugin.logger.LogWarning("Can't send map index " + Plugin.NextMapIndex + " because it is too large to send online, re-rolling...");
                Plugin.RandomBagLevel();
            }
            UnityEngine.Debug.Log($"sending map index {Plugin.NextMapIndex} in chunks now.");
            if (!Plugin.CONFIG_forceDoNotReloadMapsFromStorageOnline.Value)
            {
                //bool tryReconstructingZipArchive = false;
                Plugin.MapFileStreams[Plugin.NextMapIndex].Position = 0;
                byte[] fileBytes = new byte[Plugin.MapFileStreams[Plugin.NextMapIndex].Length];
                int numBytesToRead = (int)Plugin.MapFileStreams[Plugin.NextMapIndex].Length;
                int numBytesRead = 0;
                while (numBytesToRead > 0)
                {
                    int n = Plugin.MapFileStreams[Plugin.NextMapIndex].Read(fileBytes, numBytesRead, numBytesToRead);
                    // Break when the end of the file is reached.
                    if (n == 0)
                    {
                        break;
                    }
                    numBytesRead += n;
                    numBytesToRead -= n;
                }
                NextMapByteChunks = fileBytes.ToList();
                // try to reload from storage, if the map is non-existent try to recompress it, but if it's too large then halt and catch fire.
                // oh wait nevermind apparently I have to access the old handle that was being made.
                // if (File.Exists(Plugin.MapFilePaths[Plugin.NextMapIndex]))
                // {
                //     using (FileStream zipStream = new(Plugin.MapFilePaths[Plugin.NextMapIndex], FileMode.Open))
                //     {
                //         // copied from `Plugin.UnzipFile()`, because trying to put this into its own function appears to cause a memory leak?
                //         // or at the very least at this scale a potential memory leak is not worth the tradeoff of slightly less repetitve code.
                //         byte[] fileBytes = new byte[zipStream.Length];
                //         int numBytesToRead = (int)zipStream.Length;
                //         int numBytesRead = 0;
                //         while (numBytesToRead > 0)
                //         {
                //             int n = zipStream.Read(fileBytes, numBytesRead, numBytesToRead);
                //             // Break when the end of the file is reached.
                //             if (n == 0)
                //             {
                //                 break;
                //             }
                //             numBytesRead += n;
                //             numBytesToRead -= n;
                //         }
                        
                //         uint crc32 = Crc32.HashToUInt32(fileBytes);
                //         if (Plugin.MapCRC32Checksums[Plugin.NextMapIndex] != crc32)
                //         {
                //             Plugin.logger.LogError("CRC32 checksum mismatch! file changed between when the game launched and now! this is unsupported!");
                //             tryReconstructingZipArchive = true;
                //         }
                //         else
                //         {
                //             NextMapByteChunks = fileBytes.ToList();
                //         }
                //     }
                // }
                // else
                // {
                //     Plugin.logger.LogError("Map file apparently no longer exists.");
                //     tryReconstructingZipArchive = true;
                // }
                // if (tryReconstructingZipArchive)
                // {
                //     Plugin.logger.LogInfo("as a backup, we'll attempt to send the map by reconstructing and recompressing a new zip.");
                //     using (MemoryStream memStream = new MemoryStream())
                //     {
                //         using (ZipArchive reconstructedArchive = new ZipArchive(memStream, ZipArchiveMode.Create, true))
                //         {
                //             foreach (ZipArchiveEntry entry in Plugin.zipArchives[Plugin.NextMapIndex].Entries)
                //             {
                //                 ZipArchiveEntry newEntry = reconstructedArchive.CreateEntry(entry.FullName);
                //                 using (Stream entryStream = entry.Open())
                //                 using (Stream newEntryStream = newEntry.Open())
                //                 entryStream.CopyTo(newEntryStream);
                //             }
                //         }
                //         if (memStream.Length > MAX_FILE_SIZE)
                //         {
                //             // in theory I could try to go even deeper with the edge case handling here but like, I don't think that's super necessary.
                //             Plugin.logger.LogError("Old file has been deleted or modified and the recompressed file is too large to send, aborting...");
                //             GameSessionHandler.LeaveGame(abandonLobbyEntirely: true, sendOthersBackToAbilitySelect: true);
                //             return;
                //         }
                //         else
                //         {
                //             NextMapByteChunks = memStream.ToArray().ToList();
                //         }
                //     }
                // }
            }
            else
            {
                NextMapByteChunks = Plugin.cachedMapRawBytes[Plugin.NextMapIndex];
            }

            Plugin.logger.LogInfo("preparing to send map that is " + NextMapByteChunks.Count + " bytes large");
            if (NextMapByteChunks.Count > MAX_FILE_SIZE)
            {
                Plugin.logger.LogWarning("map file is too large to send even after this edge case was checked for! ("
                + NextMapByteChunks.Count + " bytes > " + MAX_FILE_SIZE + " bytes)");
                GameSessionHandler.LeaveGame(abandonLobbyEntirely: true, sendOthersBackToAbilitySelect: true);
                return;
            }
            else if (NextMapByteChunks.Count <= MAX_PREFERED_FILE_SIZE) // use 1 chunk
            {
                Plugin.logger.LogInfo("preparing to send map in 1 chunk");
                Host_LargeChunkSize = (uint)NextMapByteChunks.Count;
                Host_LargeChunkAmountMinus1 = 0;
                Host_SmallChunkAmount = 0;
                Host_SmallChunkSize = 0;
            }
            else if (NextMapByteChunks.Count <= MAX_PREFERED_FILE_SIZE * 256) // use the amount of MAX_PREFERED_FILE_SIZE chunks we need + 1 small chunk if needed
            {
                Plugin.logger.LogInfo("preparing to send map within <= 64 kib chunks");
                Host_LargeChunkAmountMinus1 = ((uint)NextMapByteChunks.Count / MAX_PREFERED_FILE_SIZE) - 1; // integer division, not floating point division!
                Host_SmallChunkSize = (uint)(NextMapByteChunks.Count % MAX_PREFERED_FILE_SIZE);
                if (Host_SmallChunkSize > 0)
                {
                    Host_SmallChunkAmount = 1;
                    Host_LargeChunkAmountMinus1 -= 1;
                }
                else
                {
                    Host_SmallChunkAmount = 0;
                }
                Host_LargeChunkSize = MAX_PREFERED_FILE_SIZE;
            }
            else //ie, if (NextMapByteChunks.Count > MAX_PREFERED_FILE_SIZE * 256)
            {
                // yeah apparently the division ends up being the calculation that gives the smaller size.
                // the more I think about it it's probably better that we only have 1 large chunk and 255 small chunks
                // instead of 255 large chunks and 1 small chunk in this case, because we leave the buffer less saturated.
                // COMING BACK TO THIS LATER: apparently there's an edge case at exactly the max size where Host_LargeChunkSize = 0
                // so uh, yeah.
                // maybe I should do NextMapByteChunks.Count / 255u but then that seems to lead to cases where Host_LargeChunkSize
                // can be smaller than Host_SmallChunkSize and renaming the variables and all that just for this is gonna give me a headache
                // so lets just not
                Plugin.logger.LogInfo("preparing to send map that is of an unprefered size");
                Host_SmallChunkSize = (uint)NextMapByteChunks.Count / 256u; // again, integer division, not floating point division!
                Host_LargeChunkSize = (uint)(NextMapByteChunks.Count - (Host_SmallChunkSize * 256u));
                if (Host_LargeChunkSize == 0)
                {
                    Host_LargeChunkSize = Host_SmallChunkSize;
                }
                Host_LargeChunkAmountMinus1 = 0;
                Host_SmallChunkAmount = 255;
            }
            Host_EachPlayerNextChunkNum = new();
            Host_EachPlayerLastAckedChunkNum = new();
            foreach (SteamConnection player in SteamManager.instance.connectedPlayers)
            {
                Host_EachPlayerNextChunkNum.Add(player.id, 0);
                Host_EachPlayerLastAckedChunkNum.Add(player.id, -1);
            }
            foreach (SteamId id in Host_EachPlayerLastAckedStartReq.Keys)
            {
                bool wasFound = false;
                foreach (SteamConnection player in SteamManager.instance.connectedPlayers)
                {
                    if (player.id == id)
                    {
                        wasFound = true;
                    }
                }
                if(!wasFound)
                {
                    Host_EachPlayerLastAckedStartReq.Remove(id);
                }
            }
            NewMapSendingStatusChannel.SendMessage(1);
            IsMapBeingTransmitted = true;
            //hasStartReqPacketBeenSent = false;
        }

        // apparently there isn't a built in byte entwiner?
        // welp this feels silly but I have to do it this way.
        public static byte[] SingleByteToByteArray(byte singleByte)
        {
            return [singleByte];
        }
        public static byte ByteArrayToSingleByte(byte[] byteArray)
        {
            return byteArray[0];
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
            if (!SteamManager.instance.currentLobby.IsOwnedBy(sourceInfo.Identity.SteamId))
            {
                return;
            }
            Plugin.logger.LogInfo("received chunk " + zipChunkPacket.chunkNum);
            if(Client_CurrentChunkNum != zipChunkPacket.chunkNum)
            {
                Plugin.logger.LogWarning("received chunk num " + zipChunkPacket.chunkNum + " when chunk num " + Client_CurrentChunkNum + " was expected.\n"
                    + "This shouldn't be possible unless somebody is trying to mess with map loader's netcode. If this is a bug, the game will likely softlock.");
                return;
            }
            NextMapByteChunks.AddRange(zipChunkPacket.zipChunkBytes);
            // find lobby owner's connection object because the game uses a for loop for this *every time* apparently.
            // also yeah for some reason just using the Connection object from the PacketSourceInfo instance doesn't seem to work.
            for (int i = 0; i < SteamManager.instance.connectedPlayers.Count; i++)
            {
                if (SteamManager.instance.currentLobby.IsOwnedBy(SteamManager.instance.connectedPlayers[i].id))
                {
                    Plugin.logger.LogInfo("sending ack...");
                    ChunkIDAckChannel.SendMessageTo(Client_CurrentChunkNum, SteamManager.instance.connectedPlayers[i].Connection);
                }
            }
            Client_CurrentChunkNum++;
        }
        public static void OnChunkAck(byte ackedChunk, PacketSourceInfo sourceInfo)
        {
            if (!SteamManager.LocalPlayerIsLobbyOwner)
            {
                Plugin.logger.LogInfo("CAN'T ACK THIS BECAUSE WE ARE NOT THE HOST | " + sourceInfo.SenderSteamId + " acked chunk " + ackedChunk);
                return;
            }

            Host_EachPlayerLastAckedChunkNum[sourceInfo.SenderSteamId] = ackedChunk;
            Plugin.logger.LogInfo(sourceInfo.SenderSteamId + " acked chunk " + ackedChunk);
        }
        public static void OnStartRequestAck(byte levelIdForOnlineInputs, PacketSourceInfo sourceInfo)
        {
            if (!SteamManager.LocalPlayerIsLobbyOwner)
            {
                Plugin.logger.LogInfo("CAN'T ACK THIS BECAUSE WE ARE NOT THE HOST | " + sourceInfo.SenderSteamId
                + " acked levelIdForOnlineInputs " + levelIdForOnlineInputs);
                return;
            }
            Host_EachPlayerLastAckedStartReq[sourceInfo.SenderSteamId] = levelIdForOnlineInputs;
        }
        private static void PrepareBetterStartRequestPacket(int mapIndexToPrepare)
        {
            UnityEngine.Debug.Log("preparing start request packet now.");
            UnityEngine.Debug.Log($"we have {Plugin.MapJsons.Length} maps");
            UnityEngine.Debug.Log($"we are preparing for map index {mapIndexToPrepare}");
            Dictionary<string, object> MetaData = MiniJSON.Json.Deserialize(Plugin.MetaDataJsons[mapIndexToPrepare]) as Dictionary<string, object>;
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
            // map IDs are not actually UUIDs but it's too late to rename the json property now
            Plugin.CurrentMapIntID = Convert.ToInt32(MetaData["MapUUID"]);
            SteamManager.startParameters = default(StartRequestPacket);
            if (!IsFirstMap)
            {
                SteamManager.startParameters.frameBufferSize = (byte)Host.CurrentDelayBufferSize;
            }
            ushort num = SteamManager.instance.nextStartGameSeq;
            SteamManager.instance.nextStartGameSeq = (ushort)(num + 1);
            SteamManager.startParameters.seqNum = num;
            SteamManager.startParameters.nrOfPlayers = (byte)(SteamManager.instance.connectedPlayers.Count + 1);
            SteamManager.startParameters.nrOfAbilites = (byte)Settings.Get().NumberOfAbilities;
            SteamManager.startParameters.currentLevel = GameSession.CurrentLevel();
            SteamManager.startParameters.seed = (uint)Environment.TickCount;
            SteamManager.startParameters.p1_id = SteamClient.SteamId;
            SteamManager.startParameters.p1_team = (byte)CharacterSelectHandler_online.localPlayerInit.team;
            SteamManager.startParameters.p1_color = (byte)CharacterSelectHandler_online.localPlayerInit.color;
            SteamManager.startParameters.p1_ability1 = (byte)CharacterSelectHandler_online.localPlayerInit.ability0;
            SteamManager.startParameters.p1_ability2 = (byte)CharacterSelectHandler_online.localPlayerInit.ability1;
            SteamManager.startParameters.p1_ability3 = (byte)CharacterSelectHandler_online.localPlayerInit.ability2;
            if (SteamManager.instance.connectedPlayers.Count > 0)
            {
                SteamManager.startParameters.p2_id = SteamManager.instance.connectedPlayers[0].id;
                SteamManager.startParameters.p2_team = SteamManager.instance.connectedPlayers[0].lobby_team;
                SteamManager.startParameters.p2_color = (byte)SteamManager.instance.connectedPlayers[0].lobby_color;
                SteamManager.startParameters.p2_ability1 = SteamManager.instance.connectedPlayers[0].lobby_ability1;
                SteamManager.startParameters.p2_ability2 = SteamManager.instance.connectedPlayers[0].lobby_ability2;
                SteamManager.startParameters.p2_ability3 = SteamManager.instance.connectedPlayers[0].lobby_ability3;
            }
            if (SteamManager.instance.connectedPlayers.Count > 1)
            {
                SteamManager.startParameters.p3_id = SteamManager.instance.connectedPlayers[1].id;
                SteamManager.startParameters.p3_team = SteamManager.instance.connectedPlayers[1].lobby_team;
                SteamManager.startParameters.p3_color = (byte)SteamManager.instance.connectedPlayers[1].lobby_color;
                SteamManager.startParameters.p3_ability1 = SteamManager.instance.connectedPlayers[1].lobby_ability1;
                SteamManager.startParameters.p3_ability2 = SteamManager.instance.connectedPlayers[1].lobby_ability2;
                SteamManager.startParameters.p3_ability3 = SteamManager.instance.connectedPlayers[1].lobby_ability3;
            }
            if (SteamManager.instance.connectedPlayers.Count > 2)
            {
                SteamManager.startParameters.p4_id = SteamManager.instance.connectedPlayers[2].id;
                SteamManager.startParameters.p4_team = SteamManager.instance.connectedPlayers[2].lobby_team;
                SteamManager.startParameters.p4_color = (byte)SteamManager.instance.connectedPlayers[2].lobby_color;
                SteamManager.startParameters.p4_ability1 = SteamManager.instance.connectedPlayers[2].lobby_ability1;
                SteamManager.startParameters.p4_ability2 = SteamManager.instance.connectedPlayers[2].lobby_ability2;
                SteamManager.startParameters.p4_ability3 = SteamManager.instance.connectedPlayers[2].lobby_ability3;
            }
            byte b = (byte)(SteamManager.instance.dlc.HasDLC() ? 1 : 0);
            for (int i = 0; i < SteamManager.instance.connectedPlayers.Count; i++)
            {
                if (SteamManager.instance.connectedPlayers[i].ownsFullGame)
                {
                    b = (byte)((int)b | 1 << i + 1);
                }
            }
            SteamManager.startParameters.isDemoMask = b;
            SteamManager.instance.EncodeCurrentStartParameters_forReplay(ref SteamManager.instance.networkClient.EncodedStartRequest, SteamManager.startParameters, false);
            NextLevelIdForOnlineInputs = (byte)(Plugin.CurrentLevelIdForInputsOnlineThingy + 1);
            UnityEngine.Debug.Log($"Hosting next level. new level id for inputs: {NextLevelIdForOnlineInputs}");
            // preparedBetterStartRequestPacket = new BetterStartRequestPacket
            // {
            //     startRequest = SteamManager.startParameters,
            //     MapIndex = mapIndexToPrepare, // to be removed.
            //     MapIdForInputStuff = Plugin.CurrentLevelIdForInputsOnlineThingy
            // };
            //hasBetterStartReqPacketBeenPrepared = true;
        }
        public static void SendBetterStartRequestPacket(bool prepStartReqPacketFirst = true)
        {
            if (!SteamManager.LocalPlayerIsLobbyOwner)
            {
                Plugin.logger.LogWarning("SendBetterStartRequestPacket() called somehow when we aren't the host, returning early...");
                return;
            }
            Plugin.CurrentMapIndex = Plugin.NextMapIndex;
            if (prepStartReqPacketFirst)
            {
                PrepareBetterStartRequestPacket(Plugin.CurrentMapIndex);
            }
            Plugin.logger.LogInfo("SendBetterStartRequestPacket: NextLevelIdForOnlineInputs = " + NextLevelIdForOnlineInputs);
            Plugin.CurrentLevelIdForInputsOnlineThingy = NextLevelIdForOnlineInputs;
            StartChannel.SendMessage(new BetterStartRequestPacket {
                startRequest = SteamManager.startParameters,
                MapIdForInputStuff = Plugin.CurrentLevelIdForInputsOnlineThingy
            });
            if(IsFirstMap)
            {
                CharacterSelectHandler_online.ForceStartGame();
            }
            else if (SendStartReqAndForceLoadNextLevelAfterMapSends)
            {
                // I uh, wonder why zapray programmed his game in such a way to require things like this but sure whatever fine I guess.
                List<Player> list = PlayerHandler.Get().PlayerList();
                Player player = null;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].IsLocalPlayer)
                    {
                        player = list[i];
                    }
                }
                SteamManager.instance.HostNextLevel(player, SteamManager.instance.abilityIcons);
				SteamManager.ForceLoadNextLevel();
            }
            IsFirstMap = false;
            SendStartReqAndForceLoadNextLevelAfterMapSends = false;
        }
        public static void OnNewMapSendingStatus(byte typeByte, PacketSourceInfo sourceInfo)
        {
            if (!SteamManager.instance.currentLobby.IsOwnedBy(sourceInfo.Identity.SteamId))
            {
                return;
            }
            Plugin.logger.LogInfo("OnNewMapSendingStatus: typeByte = " + typeByte);
            IsMapBeingTransmitted = typeByte == 1;
            if (IsMapBeingTransmitted)
            {
                Client_CurrentChunkNum = 0;
                Plugin.logger.LogInfo("chunk num reset");
                // reroll for a potentially higher max send rate.
                int i_retryForBetterSendRateMax = 0;
                while (i_retryForBetterSendRateMax < 10 && Steamworks.SteamNetworkingUtils.GetConfigInt(Steamworks.NetConfig.SendRateMax) < 1_000_000)
                {
                    Steamworks.SteamNetworkingUtils.SetConfigInt(Steamworks.NetConfig.SendRateMax, 268435456);
                }
            }
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
                OldZipReceivedChannel.SendMessage(payload.id);
            }
        }

        public static byte[] BetterStartRequestPacketToByteArray(BetterStartRequestPacket BetterStartRequest)
        {
            StartRequestPacket startRequest = BetterStartRequest.startRequest;
            byte[] buff = new byte[105];
            NetworkTools.EncodeStartRequest(ref buff, startRequest);
            List<byte> packet = buff.ToList();
            //packet.AddRange(BitConverter.GetBytes(BetterStartRequest.MapIndex));
            packet.Add(BetterStartRequest.MapIdForInputStuff);
            return packet.ToArray();
        }
        public static BetterStartRequestPacket ByteArrayToBetterStartRequestPacket(byte[] bytes)
        {
            List<byte> bytes2 = bytes.ToList();
            bytes2.RemoveRange(bytes2.Count - 1, 1);
            var uintConversionArray = new byte[4];
            var ulongConversionArray = new byte[8];
            var ushortConversionArray = new byte[8];
            StartRequestPacket startRequest = NetworkTools.ReadStartRequest(bytes2.ToArray(), ref uintConversionArray, ref ulongConversionArray, ref ushortConversionArray);
            // byte[] bytes3 = { bytes[bytes.Length - 5], bytes[bytes.Length - 4], bytes[bytes.Length - 3], bytes[bytes.Length - 2] };
            // if (!BitConverter.IsLittleEndian)
            // {
            //     Array.Reverse(bytes3);
            // }
            //var MapIndex = BitConverter.ToInt32(bytes3, 0);
            var MapIdForInputStuff = bytes[bytes.Length - 1];
            //UnityEngine.Debug.Log($"MapIndex is {MapIndex}");
            var betterStartRequestPacket = new BetterStartRequestPacket
            {
                startRequest = startRequest,
                //MapIndex = MapIndex,
                MapIdForInputStuff = MapIdForInputStuff
            };
            return betterStartRequestPacket;
        }
        public static void OnStartPacket(BetterStartRequestPacket payload, PacketSourceInfo sourceInfo)
        {
            if (SteamManager.instance.currentLobby.IsOwnedBy(sourceInfo.Identity.SteamId))
            {
                NetworkingStuff.ForceDoNotAttemptAnotherStartUntilMenuAwayFlag = true;
                SteamManager.startParameters = payload.startRequest;
                // this was useful with the old system for sending maps where maps were never unloaded online, but now maps are always unloaded online.
                //Plugin.CurrentMapIndex = payload.MapIndex;
                Plugin.CurrentMapIndex = 0;

                // load the new map from NextMapByteChunks, and unload any other maps.
                Plugin.zipArchives = [new ZipArchive(new MemoryStream(NextMapByteChunks.ToArray()), ZipArchiveMode.Read)]; // POSSIBLE MEMORY LEAK
                Plugin.MapJsons = [Plugin.GetFileFromZipArchive(Plugin.zipArchives[Plugin.CurrentMapIndex], Plugin.IsBoplMap)[0]];
                Plugin.MetaDataJsons = [Plugin.GetFileFromZipArchive(Plugin.zipArchives[Plugin.CurrentMapIndex], Plugin.IsMetaDataFile)[0]];
                NextMapByteChunks.Clear();

                Plugin.CurrentLevelIdForInputsOnlineThingy = payload.MapIdForInputStuff;
                UnityEngine.Debug.Log($"received start request packet. set map id for input stuff to {Plugin.CurrentLevelIdForInputsOnlineThingy}");
                UnityEngine.Debug.Log("sending start request ack now before continuing...");
                for (int i = 0; i < SteamManager.instance.connectedPlayers.Count; i++)
                {
                    if (SteamManager.instance.currentLobby.IsOwnedBy(SteamManager.instance.connectedPlayers[i].id))
                    {
                        StartRequestAckChannel.SendMessageTo(Plugin.CurrentLevelIdForInputsOnlineThingy, SteamManager.instance.connectedPlayers[i].Connection);
                    }
                }

                UnityEngine.Debug.Log($"we have {Plugin.MapJsons.Length} map(s)");
                // BetterStartRequestPacket.MapIndex has been removed.
                //UnityEngine.Debug.Log($"the host sent us map index {payload.MapIndex} (should be map {Plugin.CurrentMapIndex} in our array)");
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
                // map IDs are not actually UUIDs but it's too late to rename the json property
                Plugin.CurrentMapIntID = Convert.ToInt32(MetaData["MapUUID"]);

                SteamManager.instance.EncodeCurrentStartParameters_forReplay(ref SteamManager.instance.networkClient.EncodedStartRequest, SteamManager.startParameters, false);
                UnityEngine.Debug.Log(string.Concat(new object[] {
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
        public static void OnZipReceivedConfirmed(int payload, PacketSourceInfo sourceInfo)
        {
            if (payload == Plugin.NextMapIndex)
            {
                OLD_I_THINK_hasFullyReceivedLatestZip[sourceInfo.SenderSteamId] = true;
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
        //public int MapIndex; // this gets ignored with the new map sending system that always unloads old maps.
        public byte MapIdForInputStuff;
    }
}
