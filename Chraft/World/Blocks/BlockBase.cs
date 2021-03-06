﻿using System;
using System.Collections.Generic;
using Chraft.Entity;
using Chraft.Interfaces;
using Chraft.Net;
using Chraft.Net.Packets;
using Chraft.Plugins.Events.Args;
using Chraft.World.Blocks.Interfaces;

namespace Chraft.World.Blocks
{
    /// <summary>
    /// Represents a certain block in the world
    /// </summary>
    public struct StructBlock
    {
        public byte Type;
        public UniversalCoords Coords;
        public byte MetaData;
        public Chunk Chunk;
        public WorldManager World;

        public StructBlock(UniversalCoords coords, byte type, byte metaData, WorldManager world)
        {
            Type = type;
            Coords = coords;
            MetaData = metaData;
            World = world;
            Chunk = World.GetBlockChunk(Coords);
        }
    }

    public abstract class BlockBase : IBlockBase
    {
        /// <summary>
        /// String representation of the block name
        /// </summary>
        public string Name { get; protected set; }

        /// <summary>
        /// Block type
        /// </summary>
        public BlockData.Blocks Type { get; set; }

        /// <summary>
        /// Can we move through the block
        /// </summary>
        public bool IsAir { get; protected set; }

        /// <summary>
        /// Is the block liquid
        /// </summary>
        public bool IsLiquid { get; protected set; }

        /// <summary>
        /// Is the block solid - can be a basement for other blocks
        /// </summary>
        public bool IsSolid { get; protected set; }

        /// <summary>
        /// Opacity of the block where 0x0 is transparent and 0xf is opaque
        /// </summary>
        public byte Opacity { get; protected set; }

        /// <summary>
        /// If the block is opaque or not
        /// </summary>
        public bool IsOpaque
        {
            get { return (Opacity == 0xf); }
        }

        /// <summary>
        /// Requires single hit to destroy
        /// </summary>
        public bool IsSingleHit { get; protected set; }

        /// <summary>
        /// Can the greens (but not the crops) grow on it
        /// </summary>
        public bool IsFertile { get; protected set; }

        /// <summary>
        /// Was the block plowed and made suitable for the crops
        /// </summary>
        public bool IsPlowed { get; protected set; }

        /// <summary>
        /// Can the block be burn
        /// </summary>
        public bool IsIgnitable
        {
            get { return (BurnEfficiency > 0); }
        }

        /// <summary>
        /// Block Flammability/Burn Efficiency measured in world ticks (x0.05secs). Value / 20 => number of seconds burn time. 10secs = 1 item smelted
        /// </summary>
        public short BurnEfficiency { get; protected set; }

        /// <summary>
        /// Light emitted by the block
        /// </summary>
        public byte Luminance { get; protected set; }


        public List<ItemStack> LootTable { get; protected set; }

        /// <summary>
        /// Base contructor
        /// </summary>
        protected BlockBase()
        {
            Name = "BaseBlock";
            Type = BlockData.Blocks.Air;
            IsAir = false;
            IsLiquid = false;
            Opacity = 0xf;
            IsSolid = false;
            IsSingleHit = false;
            IsFertile = false;
            IsPlowed = false;
            BurnEfficiency = 0;
            LootTable = new List<ItemStack>();
            Luminance = 0;
        }

        /// <summary>
        /// Destroy the block
        /// </summary>
        /// <param name="block">block that has been destroyed</param>
        public virtual void Destroy(StructBlock block)
        {
            Destroy(null, block);
        }


        /// <summary>
        /// Destroy the block
        /// </summary>
        /// <param name="entity">entity who destroyed the block</param>
        /// <param name="block">block that has been destroyed</param>
        public virtual void Destroy(EntityBase entity, StructBlock block)
        {
            BlockDestroyEventArgs eventArgs = RaiseDestroyEvent(entity, block);
            if (eventArgs.EventCanceled)
                return;

            PlaySoundOnDestroy(block);

            UpdateOnDestroy(block);

            DropItems(entity, block);

            DamageItem(entity);

            NotifyNearbyBlocks(entity, block);
        }

        /// <summary>
        /// Removes the block from the world. Don't drop anything.
        /// </summary>
        /// <param name="block">block that is being removed</param>
        public virtual void Remove(StructBlock block)
        {
            UpdateOnDestroy(block);
            NotifyNearbyBlocks(null, block);
        }

        /// <summary>
        /// Spawns the block in the world (not placed by the player)
        /// </summary>
        /// <param name="block">block that is being spawned</param>
        public virtual void Spawn(StructBlock block)
        {
            UpdateOnPlace(block);
        }

        /// <summary>
        /// Notifies the nearby block that the current block has been destroyed
        /// May be used by recipient block to start the physic simulation etc
        /// </summary>
        /// <param name="entity">entity who destroyed the block</param>
        /// <param name="block">block that has been destroyed</param>
        protected virtual void NotifyNearbyBlocks(EntityBase entity, StructBlock block)
        {
            List<UniversalCoords> blocks = new List<UniversalCoords>(6);
            if (block.Coords.WorldY < 127)
                blocks.Add(UniversalCoords.FromWorld(block.Coords.WorldX, block.Coords.WorldY + 1, block.Coords.WorldZ));
            if (block.Coords.WorldY > 0)
                blocks.Add(UniversalCoords.FromWorld(block.Coords.WorldX, block.Coords.WorldY - 1, block.Coords.WorldZ));
            blocks.Add(UniversalCoords.FromWorld(block.Coords.WorldX - 1, block.Coords.WorldY, block.Coords.WorldZ));
            blocks.Add(UniversalCoords.FromWorld(block.Coords.WorldX + 1, block.Coords.WorldY, block.Coords.WorldZ));
            blocks.Add(UniversalCoords.FromWorld(block.Coords.WorldX, block.Coords.WorldY, block.Coords.WorldZ - 1));
            blocks.Add(UniversalCoords.FromWorld(block.Coords.WorldX, block.Coords.WorldY, block.Coords.WorldZ + 1));
            byte blockId = 0;
            byte blockMeta = 0;
            foreach (var coords in blocks)
            {
                blockId = block.World.GetBlockId(coords);
                blockMeta = block.World.GetBlockData(coords);
                BlockHelper.Instance(blockId).NotifyDestroy(entity, block, new StructBlock(coords, blockId, blockMeta, block.World));
            }
        }

        /// <summary>
        /// Process the notification about nearby block destruction
        /// </summary>
        /// <param name="entity">entity who destroyed the nearby block</param>
        /// <param name="sourceBlock">block that has been destroyed</param>
        /// <param name="targetBlock">block that recieves the notification</param>
        public virtual void NotifyDestroy(EntityBase entity, StructBlock sourceBlock, StructBlock targetBlock)
        { }

        /// <summary>
        /// Called when the entity touches the block - pressure plates, proximity sensors etc
        /// </summary>
        /// <param name="entity">entity who touched the block</param>
        /// <param name="block">block that has been touched</param>
        public virtual void Touch(EntityBase entity, StructBlock block) { }

        /// <summary>
        /// Places the block
        /// </summary>
        /// <param name="block">block that is being placed</param>
        /// <param name="targetBlock">block that is being targeted (aimed)</param>
        /// <param name="face">side of the target block</param>
        public virtual void Place(StructBlock block, StructBlock targetBlock, BlockFace face)
        {
            Place(null, block, targetBlock, face);
        }
        /// <summary>
        /// Places the block
        /// </summary>
        /// <param name="entity">entity who placed the block</param>
        /// <param name="block">block that is being placed</param>
        /// <param name="targetBlock">block that is being targeted (aimed)</param>
        /// <param name="face">side of the target block</param>
        public virtual void Place(EntityBase entity, StructBlock block, StructBlock targetBlock, BlockFace face)
        {
            if (!CanBePlacedOn(entity, block, targetBlock, face))
                return;

            if (!RaisePlaceEvent(entity, block))
                return;

            UpdateOnPlace(block);
            RemoveItem(entity);
        }

        /// <summary>
        /// Raises the block destruction event
        /// </summary>
        /// <param name="entity">entity who destroyed the block</param>
        /// <param name="block">block that has been destroyed</param>
        /// <returns>resulting event args</returns>
        protected virtual BlockDestroyEventArgs RaiseDestroyEvent(EntityBase entity, StructBlock block)
        {
            BlockDestroyEventArgs e = new BlockDestroyEventArgs(this, entity);
            block.World.Server.PluginManager.CallEvent(Plugins.Events.Event.BLOCK_DESTROY, e);
            return e;
        }

        /// <summary>
        /// Raises the block placement event
        /// </summary>
        /// <param name="entity">entity who placed the block</param>
        /// <param name="block">block that has been placed</param>
        /// <returns>resulting event args</returns>
        protected virtual bool RaisePlaceEvent(EntityBase entity, StructBlock block)
        {
            BlockPlaceEventArgs e = new BlockPlaceEventArgs(this, entity);
            block.World.Server.PluginManager.CallEvent(Plugins.Events.Event.BLOCK_PLACE, e);

            // Destruction made not by the living can not be interrupted?
            if (entity == null)
                return true;
            return !e.EventCanceled;
        }

        /// <summary>
        /// Plays the sound on block destruction
        /// </summary>
        /// <param name="block">block that has been destroyed</param>
        protected virtual void PlaySoundOnDestroy(StructBlock block)
        {
            foreach (Client cl in block.World.Server.GetNearbyPlayers(block.World, UniversalCoords.ToAbsWorld(block.Coords)))
            {
                cl.SendPacket(new SoundEffectPacket
                {
                    EffectID = SoundEffectPacket.SoundEffect.BLOCK_BREAK,
                    X = block.Coords.WorldX,
                    Y = (byte)block.Coords.WorldY,
                    Z = block.Coords.WorldZ,
                    SoundData = block.Type
                });
            }
        }

        /// <summary>
        /// Updates world data upon block destruction
        /// </summary>
        /// <param name="block">block that has been destroyed</param>
        protected virtual void UpdateOnDestroy(StructBlock block)
        {
            block.World.SetBlockAndData(block.Coords, (byte)BlockData.Blocks.Air, 0);
            block.Chunk.RecalculateHeight(block.Coords);
            block.Chunk.RecalculateSky(block.Coords.BlockX, block.Coords.BlockZ);
            block.Chunk.SpreadSkyLightFromBlock((byte)(block.Coords.BlockX), (byte)block.Coords.BlockY, (byte)(block.Coords.BlockZ & 0xf));
            block.World.Update(block.Coords, false);
        }

        /// <summary>
        /// Updates the world data upon block placement
        /// </summary>
        /// <param name="block">block that has been placed</param>
        protected virtual void UpdateOnPlace(StructBlock block)
        {
            block.World.SetBlockAndData(block.Coords, block.Type, block.MetaData);
            block.World.Update(block.Coords, false);
        }

        /// <summary>
        /// Invoked to drop the loot after block destruction
        /// </summary>
        /// <param name="block">block that has been destroyed</param>
        protected virtual void DropItems(StructBlock block)
        {
            DropItems(null, block);
        }

        /// <summary>
        /// Invoked to drop the loot after block destruction
        /// </summary>
        /// <param name="entity">entity that destroyed the block</param>
        /// <param name="block">block that has been destroyed</param>
        protected virtual void DropItems(EntityBase entity, StructBlock block)
        {
            if (LootTable != null && LootTable.Count > 0)
            {
                foreach (var lootEntry in LootTable)
                {
                    if (lootEntry.Count > 0)
                        block.World.Server.DropItem(block.World, block.Coords, lootEntry);
                }              
            }
        }

        /// <summary>
        /// Removes the active item from inventory when block is placed
        /// </summary>
        /// <param name="entity">the entity who placed the block</param>
        protected virtual void RemoveItem(EntityBase entity)
        {
            Player player = entity as Player;
            if (player != null && player.GameMode == 0)
                player.Inventory.RemoveItem(player.Inventory.ActiveSlot);
        }

        /// <summary>
        /// Damages the active item in the inventory when the block is destroyed
        /// </summary>
        /// <param name="entity">the entity who destroyed the block</param>
        protected virtual void DamageItem(EntityBase entity)
        {
            Player player = entity as Player;
            if (player != null && player.GameMode == 0)
                player.Inventory.DamageItem(player.Inventory.ActiveSlot);
        }

        /// <summary>
        /// Checks if the block can be placed next to the target one
        /// </summary>
        /// <param name="who">the entity who places the block</param>
        /// <param name="block">the block being placed</param>
        /// <param name="targetBlock">the block being targeted (aimed)</param>
        /// <param name="targetSide">the side of the target block</param>
        /// <returns>true if the block can be placed, false otherwise</returns>
        protected virtual bool CanBePlacedOn(EntityBase who, StructBlock block, StructBlock targetBlock, BlockFace targetSide)
        {
            BlockBase tBlock = BlockHelper.Instance(targetBlock.Type);
            if (!tBlock.IsSolid)
                return false;

            byte originalBlock = block.World.GetBlockId(block.Coords);

            if ( originalBlock != (byte)BlockData.Blocks.Air &&
                originalBlock != (byte)BlockData.Blocks.Water &&
                originalBlock != (byte)BlockData.Blocks.Still_Water &&
                originalBlock != (byte)BlockData.Blocks.Lava &&
                originalBlock != (byte)BlockData.Blocks.Still_Lava)
                return false;

            // We can't place the solid blocks on the player position (both feets and head)
            // TODO: Improve collision detection. Now the player can be partially in the block when it is placed
            if (!BlockHelper.Instance(block.Type).IsAir && !BlockHelper.Instance(block.Type).IsLiquid)
                foreach (Client c in block.World.Server.GetNearbyPlayers(block.World, UniversalCoords.ToAbsWorld(block.Coords)))
                {
                    if (c.Owner.Position.BlockX == block.Coords.WorldX && c.Owner.Position.BlockZ == block.Coords.WorldZ &&
                    (c.Owner.Position.BlockY == block.Coords.WorldY || c.Owner.Position.BlockY + 1 == block.Coords.WorldY))
                        return false;
                }

            return true;
        }


    }
}
