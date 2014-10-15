﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Craft.Net.Common;
using Craft.Net.Logic.Windows;
using MiNET.Net;
using MiNET.Utils;
using MiNET.Worlds;

namespace MiNET
{
	public enum BlockFace
	{
		NegativeY = 0,
		PositiveY = 1,
		NegativeZ = 2,
		PositiveZ = 3,
		NegativeX = 4,
		PositiveX = 5
	}

	public class Player
	{
		private readonly MiNetServer _server;
		private readonly IPEndPoint _endpoint;
		private Dictionary<string, ChunkColumn> _chunksUsed;
		private Level _level;
		private short _mtuSize;
		private List<Player> _entities;
		private int _reliableMessageNumber;
		private int _sequenceNumber;
		private object _sequenceNumberSync = new object();

		private BackgroundWorker _worker;
		private Coordinates2D _currentChunkPosition;

		public bool IsConnected { get; set; }

		public MetadataSlots Armor { get; private set; }
		public MetadataSlots Items { get; private set; }
		public MetadataInts ItemHotbar { get; private set; }
		public MetadataSlot ItemInHand { get; private set; }

		public DateTime LastUpdatedTime { get; private set; }
		public PlayerPosition3D KnownPosition { get; private set; }
		public bool IsSpawned { get; private set; }
		public string Username { get; private set; }

		public InventoryWindow Inventory { get; set; }

		public Player(MiNetServer server, IPEndPoint endpoint, Level level, short mtuSize)
		{
			_server = server;
			_endpoint = endpoint;
			_level = level;
			_mtuSize = mtuSize;
			_chunksUsed = new Dictionary<string, ChunkColumn>();
			_entities = new List<Player>();
			Inventory = new InventoryWindow();
			AddEntity(this); // Make sure we are entity with ID == 0;
			IsSpawned = false;
			KnownPosition = new PlayerPosition3D
			{
				X = _level.SpawnPoint.X,
				Y = _level.SpawnPoint.Y,
				Z = _level.SpawnPoint.Z,
				Yaw = 91,
				Pitch = 28,
				BodyYaw = 91
			};

			Armor = new MetadataSlots();
			Armor[0] = new MetadataSlot(new ItemStack(302, 3));
			Armor[1] = new MetadataSlot(new ItemStack(303, 3));
			Armor[2] = new MetadataSlot(new ItemStack(304, 3));
			Armor[3] = new MetadataSlot(new ItemStack(305, 3));

			Items = new MetadataSlots();
			for (byte i = 0; i < 35; i++)
			{
				Items[i] = new MetadataSlot(new ItemStack(i, 1));
			}
			Items[0] = new MetadataSlot(new ItemStack(41, 1));
			Items[1] = new MetadataSlot(new ItemStack(42, 1));
			Items[2] = new MetadataSlot(new ItemStack(57, 1));
			Items[3] = new MetadataSlot(new ItemStack(305, 3));

			ItemHotbar = new MetadataInts();
			for (byte i = 0; i < 6; i++)
			{
				ItemHotbar[i] = new MetadataInt(-1);
			}
			ItemHotbar[0] = new MetadataInt(9);
			ItemHotbar[1] = new MetadataInt(10);
			ItemHotbar[2] = new MetadataInt(11);
			ItemHotbar[3] = new MetadataInt(12);
			ItemHotbar[4] = new MetadataInt(13);
			ItemHotbar[5] = new MetadataInt(14);

			ItemInHand = new MetadataSlot(new ItemStack(-1));
			IsConnected = true;
		}


		public void HandlePackage(Package message)
		{
			if (typeof (McpeContainerSetSlot) == message.GetType())
			{
				var msg = (McpeContainerSetSlot) message;
				switch (msg.windowId)
				{
					case 0:
						Items[(byte) msg.slot] = new MetadataSlot(new ItemStack(msg.itemId, (sbyte) msg.itemCount, msg.itemDamage));
						break;
					case 0x78:
						Armor[(byte) msg.slot] = new MetadataSlot(new ItemStack(msg.itemId, (sbyte) msg.itemCount, msg.itemDamage));
						break;
				}
				_level.RelayBroadcast(this, new McpePlayerArmorEquipment()
				{
					entityId = 0,
					helmet = (byte) (((MetadataSlot) Armor[0]).Value.Id - 256),
					chestplate = (byte) (((MetadataSlot) Armor[1]).Value.Id - 256),
					leggings = (byte) (((MetadataSlot) Armor[2]).Value.Id - 256),
					boots = (byte) (((MetadataSlot) Armor[3]).Value.Id - 256)
				});

				_level.RelayBroadcast(this, new McpePlayerEquipment()
				{
					entityId = 0,
					item = ItemInHand.Value.Id,
					meta = ItemInHand.Value.Metadata,
					slot = 0
				});
			}

			if (typeof (McpePlayerEquipment) == message.GetType())
			{
				var msg = (McpePlayerEquipment) message;
				ItemInHand.Value.Id = msg.item;
				ItemInHand.Value.Metadata = msg.meta;

				_level.RelayBroadcast(this, msg);
			}

			if (typeof (McpePlayerArmorEquipment) == message.GetType())
			{
				_level.RelayBroadcast(this, (McpePlayerArmorEquipment) message);
			}

			if (typeof (McpeUpdateBlock) == message.GetType())
			{
				// Don't use
			}

			if (typeof (McpeRemoveBlock) == message.GetType())
			{
				var msg = (McpeRemoveBlock) message;
				_level.BreakBlock(_level, this, new Coordinates3D(msg.x, msg.y, msg.z));
			}

			if (typeof (McpeAnimate) == message.GetType())
			{
				_level.RelayBroadcast(this, (McpeAnimate) message);
			}

			if (typeof (McpeUseItem) == message.GetType())
			{
				HandleUseItem((McpeUseItem) message);
			}

			if (typeof (ConnectedPing) == message.GetType())
			{
				var msg = (ConnectedPing) message;

				SendPackage(new ConnectedPong
				{
					sendpingtime = msg.sendpingtime,
					sendpongtime = DateTimeOffset.UtcNow.Ticks/TimeSpan.TicksPerMillisecond
				});

				return;
			}

			if (typeof (ConnectedPing) == message.GetType())
			{
				var msg = (ConnectedPing) message;

				SendPackage(new ConnectedPong
				{
					sendpingtime = msg.sendpingtime,
					sendpongtime = DateTimeOffset.UtcNow.Ticks/TimeSpan.TicksPerMillisecond
				});

				return;
			}

			if (typeof (ConnectionRequest) == message.GetType())
			{
				var msg = (ConnectionRequest) message;
				var response = new ConnectionRequestAcceptedManual((short) this._endpoint.Port, msg.timestamp);
				response.Encode();

				SendPackage(response);

				return;
			}

			if (typeof (DisconnectionNotification) == message.GetType())
			{
				IsConnected = false;
				_level.RemovePlayer(this);
				return;
			}

			if (typeof (McpeMessage) == message.GetType())
			{
				var msg = (McpeMessage) message;
				string text = msg.message;
				_level.BroadcastTextMessage(text);
				return;
			}

			if (typeof (McpeRemovePlayer) == message.GetType())
			{
				// Do nothing right now, but should clear out the entities and stuff
				// from this players internal structure.
				return;
			}

			if (typeof (McpeLogin) == message.GetType())
			{
				var msg = (McpeLogin) message;
				Username = msg.username;
				SendPackage(new McpeLoginStatus {status = 0});

				// Start game
				SendStartGame();
				SendSetTime();
				SendSetSpawnPosition();
				SendSetHealth();
				SendChunksForKnownPosition();
				LastUpdatedTime = DateTime.Now;

				return;
			}

			if (typeof (McpeMovePlayer) == message.GetType())
			{
				var msg = (McpeMovePlayer) message;

				KnownPosition = new PlayerPosition3D(msg.x, msg.y, msg.z) {Pitch = msg.pitch, Yaw = msg.yaw, BodyYaw = msg.bodyYaw};
				LastUpdatedTime = DateTime.Now;

				SendChunksForKnownPosition();

				return;
			}

			if (typeof (McpeInteractPacket) == message.GetType())
			{
				HandleInteract((McpeInteractPacket) message);
				return;
			}
		}

		private void HandleInteract(McpeInteractPacket msg)
		{
			Player target = _entities[msg.targetEntityId];

			_level.RelayBroadcast(target, new McpeEntityEventPacket()
			{
				entityId = 0,
				eventId = 2
			});
		}

		private void HandleUseItem(McpeUseItem message)
		{
			if (message.face <= 5)
			{
				_level.RelayBroadcast(this, new McpeAnimate()
				{
					actionId = 1,
					entityId = 0
				});

				Debug.WriteLine("Use item: {0}", message.item);
				_level.Interact(_level, this, new Coordinates3D(message.x, message.y, message.z), message.meta, (BlockFace) message.face);
			}
			else
			{
				Debug.WriteLine("No face - Use item: {0}", message.item);
			}
		}

		public byte GetDirection()
		{
			return DirectionByRotationFlat(KnownPosition.Yaw);
		}

		public static byte DirectionByRotationFlat(float yaw)
		{
			byte direction = (byte) ((int) Math.Floor((yaw*4F)/360F + 0.5D) & 0x03);
			switch (direction)
			{
				case 0:
					return 1; // West
				case 1:
					return 2; // North
				case 2:
					return 3; // East
				case 3:
					return 0; // South 
			}
			return 0;
		}

		private void SendStartGame()
		{
			SendPackage(new McpeStartGame
			{
				seed = 1406827239,
				generator = 1,
				gamemode = (int) _level.GameMode,
				entityId = GetEntityId(this),
				spawnX = (int) KnownPosition.X,
				spawnY = (int) KnownPosition.Y,
				spawnZ = (int) KnownPosition.Z,
				x = KnownPosition.X,
				y = KnownPosition.Y,
				z = KnownPosition.Z
			});
		}

		private void SendSetSpawnPosition()
		{
			SendPackage(new McpeSetSpawnPosition
			{
				x = _level.SpawnPoint.X,
				y = (byte) _level.SpawnPoint.Y,
				z = _level.SpawnPoint.Z
			});
		}

		public void SendChunksForKnownPosition(bool force = false)
		{
			int centerX = (int) KnownPosition.X/16;
			int centerZ = (int) KnownPosition.Z/16;

			if (!force && IsSpawned && _currentChunkPosition == new Coordinates2D(centerX, centerZ)) return;

			_currentChunkPosition.X = centerX;
			_currentChunkPosition.Z = centerZ;

			_worker = new BackgroundWorker();
			_worker.WorkerSupportsCancellation = true;
			_worker.DoWork += delegate(object sender, DoWorkEventArgs args)
			{
				BackgroundWorker worker = sender as BackgroundWorker;
				int count = 0;
				foreach (var chunk in _level.GenerateChunks((int) KnownPosition.X, (int) KnownPosition.Z, force ? new Dictionary<string, ChunkColumn>() : _chunksUsed))
				{
					if (worker.CancellationPending)
					{
						args.Cancel = true;
						break;
					}
					SendPackage(new McpeFullChunkData {chunkData = chunk.GetBytes()});
					Thread.Yield();

					if (count == 56 & !IsSpawned)
					{
						InitializePlayer();

						IsSpawned = true;
						_level.AddPlayer(this);
					}

					count++;
				}
			};

			_worker.RunWorkerAsync();
		}

		private void SendSetHealth()
		{
			SendPackage(new McpeSetHealth {health = 20});
		}

		public void SendSetTime()
		{
			// started == true ? 0x80 : 0x00);
			SendPackage(new McpeSetTime {time = _level.CurrentWorldTime, started = (byte) (_level.WorldTimeStarted ? 0x80 : 0x00)});
		}

		private void InitializePlayer()
		{
			//send time again
			SendSetTime();

			// Teleport user (MovePlayerPacket) teleport=1
			SendPackage(new McpeMovePlayer
			{
				entityId = GetEntityId(this),
				x = KnownPosition.X,
				y = KnownPosition.Y,
				z = KnownPosition.Z,
				yaw = KnownPosition.Yaw,
				pitch = KnownPosition.Pitch,
				bodyYaw = KnownPosition.BodyYaw,
				teleport = 0x80
			});

			SendPackage(new McpeAdventureSettings {flags = 0x20});

			SendPackage(new McpeContainerSetContent
			{
				windowId = 0,
				slotData = Items,
				hotbarData = ItemHotbar
			});

			SendPackage(new McpeContainerSetContent
			{
				windowId = 0x78, // Armor windows ID
				slotData = Armor,
				hotbarData = null
			});
		}

		public void SendAddForPlayer(Player player)
		{
			if (player == this) return;

			SendPackage(new McpeAddPlayer
			{
				clientId = 0,
				username = player.Username,
				entityId = GetEntityId(player),
				x = player.KnownPosition.X,
				y = player.KnownPosition.Y,
				z = player.KnownPosition.Z,
				yaw = (byte) player.KnownPosition.Yaw,
				pitch = (byte) player.KnownPosition.Pitch,
				metadata = new byte[0]
			});

			SendEquipmentForPlayer(player);

			SendArmorForPlayer(player);
		}

		private void SendEquipmentForPlayer(Player player)
		{
			SendPackage(new McpePlayerEquipment()
			{
				entityId = GetEntityId(player),
				item = player.ItemInHand.Value.Id,
				meta = player.ItemInHand.Value.Metadata,
				slot = 0
			});
		}

		private void SendArmorForPlayer(Player player)
		{
			SendPackage(new McpePlayerArmorEquipment()
			{
				entityId = GetEntityId(player),
				helmet = (byte) (((MetadataSlot) Armor[0]).Value.Id - 256),
				chestplate = (byte) (((MetadataSlot) Armor[1]).Value.Id - 256),
				leggings = (byte) (((MetadataSlot) Armor[2]).Value.Id - 256),
				boots = (byte) (((MetadataSlot) Armor[3]).Value.Id - 256)
			});
		}

		public void SendRemovePlayer(Player player)
		{
			if (player == this) return;

			SendPackage(new McpeRemovePlayer
			{
				clientId = 0,
				entityId = GetEntityId(player)
			});
		}

		public void SendMovementForPlayer(Player player)
		{
			if (player == this) return;

			var knownPosition = player.KnownPosition;

			SendPackage(new McpeMovePlayer
			{
				entityId = GetEntityId(player),
				x = knownPosition.X,
				y = knownPosition.Y,
				z = knownPosition.Z,
				yaw = knownPosition.Yaw,
				pitch = knownPosition.Pitch,
				bodyYaw = knownPosition.BodyYaw,
				teleport = 0
			});
		}

		/// <summary>
		///     Very imporatnt litle method. This does all the sending of packages for
		///     the player class. Treat with respect!
		/// </summary>
		/// <param name="package"></param>
		public void SendPackage(Package package)
		{
			if (!IsConnected) return;

			lock (_sequenceNumberSync)
			{
				int numberOfMessages = _server.SendPackage(_endpoint, package, _mtuSize, _sequenceNumber, _reliableMessageNumber);
				_sequenceNumber += numberOfMessages;
				_reliableMessageNumber += numberOfMessages;
			}
		}

		private void AddEntity(Player player)
		{
			int entityId = _entities.IndexOf(player);
			if (entityId != -1)
			{
				// Allready exist				
				if (entityId != 0 && player == this)
				{
					// If this is the actual player, it should always be a 0
					_entities.Remove(player);
					_entities.Insert(0, player);
				}
			}
			else
			{
				_entities.Add(player);
			}
		}

		public int GetEntityId(Player player)
		{
			int entityId = _entities.IndexOf(player);
			if (entityId == -1)
			{
				AddEntity(player);
				entityId = _entities.IndexOf(player);
			}

			return entityId;
		}
	}
}