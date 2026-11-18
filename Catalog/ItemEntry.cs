using System;
using System.Collections.Generic;

namespace ItemCatalog.Catalog
{
    /// <summary>图鉴里的一条物品记录（阶段一只需展示用的字段）。</summary>
    internal sealed class ItemEntry
    {
        public string Id;
        public string Name;
        public string Desc;
        public long Value;
        public List<string> Types;

        /// <summary>原版："图集/精灵名"；模组物品："modId:精灵名"（仅供日志与兜底取图）。</summary>
        public string SpriteKey;

        /// <summary>GameItem.spriteAtlasPath（原版图集名，模组物品通常为空）。</summary>
        public string SpriteAtlas;

        /// <summary>GameItem.spritePath（精灵名 / 文件相对路径）。</summary>
        public string SpriteName;

        public string ModId;
        public string ModName;

        /// <summary>构建索引时实例化的物品对象，供原生 tooltip 复用（只读使用）。</summary>
        public GameItem Item;

        /// <summary>来自 UnusedDirectory 或标记为调试物品，默认隐藏。</summary>
        public bool Unused;

        /// <summary>
        /// 有限变体展开后的序号（0..N-1）；-1 表示这一条代表整件物品本身
        /// （无变体，或只有连续随机值需要轮播）。
        /// </summary>
        public int VariantIndex = -1;

        /// <summary>界面/缓存的唯一键：展开条目带上变体序号，避免 N 个格子互相覆盖。</summary>
        public string Key
        {
            get { return VariantIndex < 0 ? Id : Id + "#" + VariantIndex; }
        }

        /// <summary>
        /// 「拿取物品」时用来造新实例的工厂（特殊格子：液体条目等）。
        /// 有它就不走 CreateCellItem / ItemSpawner 那条常规路径。
        /// </summary>
        public Func<GameItem> TakeFactory;

        /// <summary>
        /// 由模组通过 ICatalogItemProvider 注入的条目（注册表里查不到）。
        /// 这类物品的原生提示框链路要靠标识回查注册表，会退化成只显示类型与价格，
        /// 所以图鉴对它们直接走自己的兜底构建（名称/描述/来源/ID 都在）。
        /// </summary>
        public bool FromProvider;

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? Id : Name; }
        }

        /// <summary>
        /// 生成「第 index 个变体」那一格：字段全部沿用，只换 VariantIndex，
        /// 并把 Item 置空——真正的实例由渲染阶段按需创建（避免启动时一次性造几百个物品）。
        /// </summary>
        public ItemEntry CloneVariant(int index)
        {
            return new ItemEntry
            {
                Id = Id,
                Name = Name,
                Desc = Desc,
                Value = Value,
                Types = Types,
                SpriteKey = SpriteKey,
                SpriteAtlas = SpriteAtlas,
                SpriteName = SpriteName,
                ModId = ModId,
                ModName = ModName,
                Item = null,
                Unused = Unused,
                FromProvider = FromProvider,
                VariantIndex = index
            };
        }
    }
}
