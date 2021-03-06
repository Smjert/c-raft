﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Chraft.Entity;
using Chraft.Interfaces;
using Chraft.Plugins.Events.Args;

namespace Chraft.World.Blocks
{
    class BlockJukebox : BlockBase
    {
        public BlockJukebox()
        {
            Name = "Jukebox";
            Type = BlockData.Blocks.Jukebox;
            IsSolid = true;
            LootTable.Add(new ItemStack((short)Type, 1));
            BurnEfficiency = 300;
        }
    }
}
