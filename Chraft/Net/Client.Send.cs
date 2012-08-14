#region C#raft License
// This file is part of C#raft. Copyright C#raft Team 
// 
// C#raft is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.
#endregion
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Chraft.Entity;
using Chraft.Interfaces;
using Chraft.Net.Packets;
using Chraft.PluginSystem.Net;
using Chraft.PluginSystem.Server;
using Chraft.Utilities;
using Chraft.Utilities.Coords;
using Chraft.World;
using Chraft.Utilities.Config;
using System.Threading;
using Chraft.World.Blocks;
using Chraft.World.Weather;
using Chraft.PluginSystem;

namespace Chraft.Net
{
    public partial class Client : IClient
    {
        public ConcurrentQueue<Packet> PacketsToBeSent = new ConcurrentQueue<Packet>();
        public ConcurrentQueue<Chunk> ChunksToBeSent = new ConcurrentQueue<Chunk>();

        private int _chunkTimerRunning;

        private int _TimesEnqueuedForSend;
        private Timer _chunkSendTimer;

        private DateTime _lastChunkTimerStart = DateTime.MinValue;
        private int _startDelay;

        public byte[] Token { get; set; }

        internal void SendPacket(IPacket iPacket)
        {
            Packet packet = iPacket as Packet;
            if (!Running)
                return;

            if (packet.Logger == null)
                packet.Logger = Server.Logger;

            PacketsToBeSent.Enqueue(packet);

            int newValue = Interlocked.Increment(ref _TimesEnqueuedForSend);

            if (newValue == 1)
            {
                Server.SendClientQueue.Enqueue(this);
                
            }

            Server.NetworkSignal.Set();

            //Logger.Log(Chraft.LogLevel.Info, "Sending packet: {0}", packet.GetPacketType().ToString());           
        }

        private void Send_Async(byte[] data)
        {
            if (!Running || !_socket.Connected)
            {
                DisposeSendSystem();
                return;
            }

            if (data[0] == (byte)PacketType.Disconnect)
                _sendSocketEvent.Completed += Disconnected;

            _sendSocketEvent.SetBuffer(data, 0, data.Length);
            bool pending = _socket.SendAsync(_sendSocketEvent);
            if (!pending)
                Send_Completed(null, _sendSocketEvent);
        }
        
        private void Send_Sync(byte[] data)
        {
            if (!Running || !_socket.Connected)
            {
                DisposeSendSystem();
                return;
            }
            try
            {
                _socket.Send(data, 0, data.Length, 0);

                if (DateTime.Now + TimeSpan.FromSeconds(5) > _nextActivityCheck)
                    _nextActivityCheck = DateTime.Now + TimeSpan.FromSeconds(5);
            }
            catch (Exception)
            {
                Stop();
            }         
        }

        internal void Send_Sync_Packet(Packet packet)
        {
            packet.Write();
            Send_Sync(packet.GetBuffer());
            packet.Release();
        }

        internal void Send_Start()
        {
            if (!Running || !_socket.Connected)
            {
                DisposeSendSystem();
                return;
            }

            Packet packet = null;
            try
            {
                ByteQueue byteQueue = new ByteQueue();
                int length = 0;
                    while (!PacketsToBeSent.IsEmpty && length <= 1024)
                    {
                        if (!PacketsToBeSent.TryDequeue(out packet))
                        {
                            Interlocked.Exchange(ref _TimesEnqueuedForSend, 0);
                            return;
                        }

                        if (!packet.Shared)
                            packet.Write();

                        byte[] packetBuffer = packet.GetBuffer();
                        length += packetBuffer.Length;

                        byteQueue.Enqueue(packetBuffer, 0, packetBuffer.Length);
                        packet.Release();

                    }
                if(Encrypter != null)
                    Encrypter.TransformBlock(byteQueue.UnderlyingBuffer, 0, byteQueue.UnderlyingBuffer.Length,
                                         byteQueue.UnderlyingBuffer, 0);
                if (byteQueue.Length > 0)
                {
                    byte[] data = new byte[length];
                    byteQueue.Dequeue(data, 0, data.Length);
                    Send_Async(data);
                }
                else
                {
                    Interlocked.Exchange(ref _TimesEnqueuedForSend, 0);

                    if (!PacketsToBeSent.IsEmpty)
                    {
                        int newValue = Interlocked.Increment(ref _TimesEnqueuedForSend);

                        if (newValue == 1)
                        {
                            Server.SendClientQueue.Enqueue(this);
                            Server.NetworkSignal.Set();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                MarkToDispose();
                DisposeSendSystem();
                if(packet != null)
                    Logger.Log(LogLevel.Error, "Sending packet: {0}", packet.ToString());
                Logger.Log(LogLevel.Error, e.ToString());

                // TODO: log something?
            }
            
        }

        private void Send_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (e.Buffer[0] == (byte)PacketType.Disconnect)
                e.Completed -= Disconnected;
            if (!Running)
                DisposeSendSystem();
            else if(e.SocketError != SocketError.Success)
            {
                MarkToDispose();
                DisposeSendSystem();
                _nextActivityCheck = DateTime.MinValue;
            }
            else
            {
                if (DateTime.Now + TimeSpan.FromSeconds(5) > _nextActivityCheck)
                    _nextActivityCheck = DateTime.Now + TimeSpan.FromSeconds(5);
                Send_Start();
            }
        }

        internal void SendPulse()
        {
            if (_player != null && _player.LoggedIn)
            {
                SendPacket(new TimeUpdatePacket
                {
                    Time = _player.World.Time
                });
                //_player.SynchronizeEntities();
            }
        }

        internal void SendBlock(int x, int y, int z, byte type, byte data)
        {
            if (_player.LoggedIn)
            {
                SendPacket(new BlockChangePacket
                {
                    Data = data,
                    Type = type,
                    X = x,
                    Y = (sbyte) y,
                    Z = z
                });
            }
        }

        private void SendMotd()
        {
            string MOTD = ChraftConfig.MOTD.Replace("%u", _player.DisplayName);
            SendMessage(MOTD);
        }


        #region Login

        internal void SendEncryptionRequest()
        {
            byte[] publicKey = new byte[]{0x30,0x81,0x9F,0x30,0x0D,0x06,0x09,0x2A,0x86,0x48,0x86,0xF7,0x0D,0x01,0x01,0x01,0x05,0x00,
                0x03,0x81,0x8D,0x00,0x30,0x81,0x89,0x02,0x81,0x81,0x00,0xD2,0xFB,0x2E,0x72,0x72,0xAA,0xBB,0x9C,0xBB,0x9C,0x1A,0x09,
                0x1A,0xE3,0x79,0xEB,0x68,0x2D,0xD5,0xFF,0xF0,0xC4,0xD7,0xDD,0xBA,0x29,0xE6,0x25,0x3D,0x79,0x06,0x16,0x5C,0x3A,0x32,
                0xA4,0x0D,0x45,0x4D,0xCD,0x65,0x18,0x2D,0x18,0xB8,0xF7,0x16,0xDB,0x51,0x84,0x09,0x60,0x68,0x66,0x98,0x37,0xFE,0x38,
                0xA9,0x1C,0x2C,0xA6,0xD3,0x4D,0xFA,0x87,0xCD,0x89,0xAE,0xC1,0xD4,0x86,0xD2,0x29,0xF7,0xB7,0x18,0x5F,0x19,0xEC,0x10,
                0xF9,0x0F,0xBD,0x3C,0x8B,0xF0,0x73,0x24,0xA3,0x50,0x1F,0xC4,0x48,0xAA,0xD8,0x08,0x11,0x3D,0x29,0x22,0xA9,0xAE,0x4D,
                0x32,0xF2,0x7E,0xE7,0xE6,0x64,0x38,0x92,0x92,0x68,0x72,0x4F,0x97,0x00,0x35,0x7A,0x8D,0x46,0x9B,0x29,0x98,0xD2,0x8F,
                0x71,0x02,0x03,0x01,0x00,0x01};
                
                //PacketCryptography.PublicKeyToAsn1(Server.ServerKey);
            short keyLength = (short)publicKey.Length;
            byte[] token = PacketCryptography.GetRandomToken();
            short tokenLength = (short)token.Length;
            Send_Sync_Packet(new EncryptionKeyRequest
                                 {
                                     ServerId = Server.ServerHash,
                                     PublicKey = publicKey,
                                     PublicKeyLength = keyLength,
                                     VerifyToken = token,
                                     VerifyTokenLength = tokenLength
                                 });
            Token = token;
        }
        internal void SendLoginRequest()
        {
            Send_Sync_Packet(new LoginRequestPacket
            {
                ProtocolOrEntityId = _player.EntityId,
                Dimension = _player.World.Dimension,
                WorldHeight = 128,
                MaxPlayers = 50,
                Difficulty = 2
            });
        }

        public void SendInitialTime(bool async = true)
        {
            Packet packet = new TimeUpdatePacket
            {
                Time = _player.World.Time
            };

            if (async)
                SendPacket(packet);
            else
                Send_Sync_Packet(packet);
        }

        internal void SendInitialPosition(bool async = true)
        {
            if(async)
                SendPacket(new PlayerPositionRotationPacket
                {
                    X = _player.Position.X,
                    Y = _player.Position.Y + this._player.EyeHeight,
                    Z = _player.Position.Z,
                    Yaw = (float)_player.Yaw,
                    Pitch = (float)_player.Pitch,
                    Stance = Stance,
                    OnGround = false
                });
            else
                Send_Sync_Packet(new PlayerPositionRotationPacket
                {
                    X = _player.Position.X,
                    Y = _player.Position.Y + this._player.EyeHeight,
                    Z = _player.Position.Z,
                    Yaw = (float)_player.Yaw,
                    Pitch = (float)_player.Pitch,
                    Stance = Stance,
                    OnGround = false
                });
        }

        internal void SendSpawnPosition(bool async = true)
        {
            Packet packet = new SpawnPositionPacket
            {
                X = _player.World.Spawn.WorldX,
                Y = _player.World.Spawn.WorldY,
                Z = _player.World.Spawn.WorldZ
            };

            if (async)
                SendPacket(packet);
            else
                Send_Sync_Packet(packet);
        }

        public bool WaitForInitialPosAck;

        internal void SendLoginSequence()
        {
            foreach(Client client in Server.GetAuthenticatedClients())
            {
                if(client.Username == Username)
                    client.Stop();
            }
            _player = new Player(Server, Server.AllocateEntity(), this);
            _player.Permissions = _player.PermHandler.LoadClientPermission(this);
            Load();

            if (!_player.World.Running)
            {
                Stop();
                return;
            }

            SendLoginRequest();
            SendSpawnPosition(false);
            SendInitialTime(false);
            _player.UpdateChunks(4, CancellationToken.None, true, false);
            SendInitialPosition(false);            
        }

        internal void SendSecondLoginSequence()
        {           
            SendInitialTime(false);
            SetGameMode();
            _player.InitializeInventory();
            _player.InitializeHealth();
            _player.OnJoined();
            Server.AddEntity(_player, false);
            Server.AddAuthenticatedClient(this);  
            SendMotd();

            StartKeepAliveTimer();
            _player.UpdateEntities();
            Server.SendEntityToNearbyPlayers(_player.World, _player);
            Server.FreeConnectionSlot();
        }

        #endregion

        #region Chunks

        internal void SendChunk(Chunk chunk, bool sync)
        {
            if (!sync)
            {
                ChunksToBeSent.Enqueue(chunk);
                int newValue = Interlocked.Increment(ref _chunkTimerRunning);

                if (newValue == 1)
                {
                    if (_lastChunkTimerStart != DateTime.MinValue)
                        _startDelay = 1000 - (int)(DateTime.Now - _lastChunkTimerStart).TotalMilliseconds;

                    if (_startDelay < 0)
                        _startDelay = 0;

                    if(_chunkSendTimer != null)
                        _chunkSendTimer.Change(_startDelay, 1000);
                }
            }
            else
                Send_Sync_Packet(new MapChunkPacket
                {
                    Chunk = chunk,
                    Logger = Logger
                });
        }

        internal void SendChunks(object state)
        {
            for (int i = 0; i < 20 && !ChunksToBeSent.IsEmpty; ++i)
            {
                Chunk chunk;
                ChunksToBeSent.TryDequeue(out chunk);

                SendPacket(new MapChunkPacket
                {
                    Chunk = chunk,
                    Logger = Logger
                });
            }

            if (ChunksToBeSent.IsEmpty)
            {
                _chunkSendTimer.Change(Timeout.Infinite, Timeout.Infinite);
                Interlocked.Exchange(ref _chunkTimerRunning, 0);
            }

            if(!ChunksToBeSent.IsEmpty)
            {
                int running = Interlocked.Exchange(ref _chunkTimerRunning, 1);

                if (running == 0)
                {
                    if (_lastChunkTimerStart != DateTime.MinValue)
                        _startDelay = 1000 - (int)(DateTime.Now - _lastChunkTimerStart).TotalMilliseconds;

                    if (_startDelay < 0)
                        _startDelay = 0;
                    _chunkSendTimer.Change(_startDelay, 1000);
                }
            }
            _lastChunkTimerStart = DateTime.Now;
        }

        internal void SendSignTexts(Chunk chunk)
        {
            foreach (var signKVP in chunk.SignsText)
            {
                int blockX = signKVP.Key >> 11;
                int blockY = (signKVP.Key & 0xFF) % 128;
                int blockZ = (signKVP.Key >> 7) & 0xF;

                UniversalCoords coords = UniversalCoords.FromBlock(chunk.Coords.ChunkX, chunk.Coords.ChunkZ, blockX, blockY, blockZ);

                string[] lines = new string[4];

                int length = signKVP.Value.Length;

                for (int i = 0; i < 4; ++i, length -= 15)
                {
                    int currentLength = length;
                    if (currentLength > 15)
                        currentLength = 15;

                    if (length > 0)
                        lines[i] = signKVP.Value.Substring(i * 15, currentLength);
                    else
                        lines[i] = "";
                }

                SendPacket(new UpdateSignPacket { X = coords.WorldX, Y = coords.WorldY, Z = coords.WorldZ, Lines = lines });
            }
        }

        #endregion


        #region Entities

        internal void SendCreateEntity(EntityBase entity)
        {
            Packet packet;
            if ((packet = (Server.GetSpawnPacket(entity) as Packet)) != null)
            {
                if (packet is NamedEntitySpawnPacket)
                {
                    SendPacket(packet);
                    for (short i = 0; i < 5; i++)
                    {
                        SendPacket(new EntityEquipmentPacket
                        {
                            EntityId = entity.EntityId,
                            Slot = i,
                            Item = ItemStack.Void
                        });
                    }
                }
            }
            else if (entity is TileEntity)
            {

            }
            else
            {
                SendEntity(entity);
                SendTeleportTo(entity);
            }               
        }

        internal void SendEntity(EntityBase entity)
        {
            SendPacket(new CreateEntityPacket
            {
                EntityId = entity.EntityId
            });
        }

        internal void SendEntityMetadata(LivingEntity entity)
        {
            SendPacket(new EntityMetadataPacket
            {
                EntityId = entity.EntityId,
                Data = entity.Data
            });
        }

        internal void SendDestroyEntity(EntityBase entity)
        {
            SendPacket(new DestroyEntityPacket
            {
                EntitiesCount = 1,
                EntitiesId = new []{entity.EntityId}
            });
        }

        internal void SendDestroyEntities(int[] entities)
        {
            SendPacket(new DestroyEntityPacket
            {
                EntitiesCount = 1,
                EntitiesId = entities
            });
        }

        internal void SendTeleportTo(EntityBase entity)
        {
            SendPacket(new EntityTeleportPacket
            {
                EntityId = entity.EntityId,
                X = entity.Position.X,
                Y = entity.Position.Y,
                Z = entity.Position.Z,
                Yaw = entity.PackedYaw,
                Pitch = entity.PackedPitch
            });

            //SendMoveBy(entity, (sbyte)((_Player.Position.X - (int)entity.Position.X) * 32), (sbyte)((_Player.Position.Y - (int)entity.Position.Y) * 32), (sbyte)((_Player.Position.Z - (int)entity.Position.Z) * 32));
        }

        internal void SendRotateBy(EntityBase entity, sbyte dyaw, sbyte dpitch)
        {
            SendPacket(new EntityLookPacket
            {
                EntityId = entity.EntityId,
                Yaw = dyaw,
                Pitch = dpitch
            });
        }

        internal void SendMoveBy(EntityBase entity, sbyte dx, sbyte dy, sbyte dz)
        {
            SendPacket(new EntityRelativeMovePacket
            {
                EntityId = entity.EntityId,
                DeltaX = dx,
                DeltaY = dy,
                DeltaZ = dz
            });
        }

        internal void SendMoveRotateBy(EntityBase entity, sbyte dx, sbyte dy, sbyte dz, sbyte dyaw, sbyte dpitch)
        {
            SendPacket(new EntityLookAndRelativeMovePacket
            {
                EntityId = entity.EntityId,
                DeltaX = dx,
                DeltaY = dy,
                DeltaZ = dz,
                Yaw = dyaw,
                Pitch = dpitch
            });
        }

        internal void SendAttachEntity(EntityBase entity, EntityBase attachTo)
        {
            SendPacket(new AttachEntityPacket
            {
                EntityId = entity.EntityId,
                VehicleId = attachTo.EntityId
            });
        }

        #endregion


        #region Clients

        internal void SendHoldingEquipment(Client c) // Updates entity holding via 0x05
        {
            SendPacket(new EntityEquipmentPacket
            {
                EntityId = c.Owner.EntityId,
                Slot = 0,
                Item = c.Owner.Inventory.ActiveItem
            });
        }

        internal void SendEntityEquipment(Client c, short slot) // Updates entity equipment via 0x05
        {
            SendPacket(new EntityEquipmentPacket
            {
                EntityId = c.Owner.EntityId,
                Slot = slot,
                Item = c.Owner.Inventory.Slots[slot]
            });
        }

        #endregion

        internal void SendWeather(WeatherState weather, UniversalCoords coords)
        {

            //throw new NotImplementedException();
        }
    }
}
