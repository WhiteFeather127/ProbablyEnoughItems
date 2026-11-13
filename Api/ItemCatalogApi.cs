using System;
using System.Collections.Generic;
using System.Text;

namespace ProbablyEnoughItems.Api
{
    /// <summary>
    /// 图鉴的公开扩展接口（JEI 风格）。
    ///
    /// 设计约束（见 agent.md）：
    /// - 这里是**对外契约**，只能新增，不能删改已有成员；
    /// - 破坏性变更必须并排新增一个新的接口类型或版本号，旧成员保留到下一个大版本；
    /// - 所有成员都在主线程调用，实现方不要在内部开线程或长期持有 GameItem。
    /// </summary>
    public static class ItemCatalogApi
    {
        /// <summary>
        /// 接口版本。新增成员时 +1，删除/改签名时必须换新类型。
        /// 3：新增 <see cref="CatalogVariantKit"/>（枚举表 + 隔离工具）与 <see cref="GetModuleShapes"/>。
        /// 4：新增 <see cref="NotifyItemsChanged()"/>（运行中补物品）与 <see cref="CatalogRefreshed"/> 事件。
        /// </summary>
        public const int ApiVersion = 4;

        private static readonly List<ICatalogVariantProvider> _variantProviders =
            new List<ICatalogVariantProvider>();

        private static readonly List<ICatalogItemProvider> _itemProviders =
            new List<ICatalogItemProvider>();

        /// <summary>
        /// 注册一个「有限变体」提供者：当某件物品的所有可能状态可以被枚举时，
        /// 图鉴会按 0..VariantCount-1 的顺序依次展示，而不是每次随机重掷。
        /// 同一个 id 重复注册时，先注册的优先。
        /// </summary>
        public static void RegisterVariantProvider(ICatalogVariantProvider provider)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (string.IsNullOrEmpty(provider.ItemId)) throw new ArgumentException("ItemId is required", "provider");

            for (int i = 0; i < _variantProviders.Count; i++)
            {
                if (string.Equals(_variantProviders[i].ItemId, provider.ItemId, StringComparison.Ordinal))
                    return;
            }

            _variantProviders.Add(provider);
        }

        /// <summary>
        /// 注册一个「额外条目」提供者：把注册表之外、但希望出现在图鉴里的物品交出来。
        /// 返回的 GameItem 由调用方负责构造，图鉴只读取、不负责销毁。
        /// </summary>
        public static void RegisterItemProvider(ICatalogItemProvider provider)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            _itemProviders.Add(provider);
        }

        // ---------------------------------------------------------------- 运行中变更通知

        private static bool _itemsChangedPending;
        private static string _itemsChangedReason;

        /// <summary>
        /// 通知图鉴「物品清单变了」：运行中新建物品、往目录里补条目，
        /// 或者某件物品的变体空间变大（比如现在能展开成 5 格）时调用。
        ///
        /// 图鉴**不会**在这行调用里同步干活（这时你可能正处在游戏的目录遍历 / 生成流程中），
        /// 只记一个标记，在之后的空闲节拍里增量补入新条目并预热图标；
        /// 同一帧内多次调用会被合并成一次。
        ///
        /// 适用场景：模组在玩家进店之后才根据存档内容动态造物品。
        /// 如果你在加载阶段就注册好了，直接调 <see cref="RegisterItemProvider"/> 即可，不需要这个。
        ///
        /// 边界：
        /// - 图鉴**不替你注册目录**。要让游戏能按 id 重建物品（存档读回、拖拽落地都依赖它），
        ///   必须自己 <c>DirectoryMaster.AddDirectory</c> + <c>Add(id, factory)</c>。
        /// - 只支持「新增 / 变体变多」。删掉已有物品的情况会在下次打开图鉴时整表重建（较慢）。
        /// </summary>
        public static void NotifyItemsChanged()
        {
            NotifyItemsChanged(null);
        }

        /// <summary>同上，带一个原因字符串，便于在日志里定位是谁触发的。</summary>
        public static void NotifyItemsChanged(string reason)
        {
            _itemsChangedPending = true;
            if (!string.IsNullOrEmpty(reason)) _itemsChangedReason = reason;
        }

        /// <summary>图鉴完成一次增量补入或整表重建后触发，参数为当前可见条目数。</summary>
        public static event Action<int> CatalogRefreshed;

        /// <summary>取出「有变更待处理」标记（取走即清除）。</summary>
        internal static bool ConsumeItemsChanged(out string reason)
        {
            reason = _itemsChangedReason;
            _itemsChangedReason = null;

            if (!_itemsChangedPending) return false;

            _itemsChangedPending = false;
            return true;
        }

        /// <summary>通知订阅者图鉴已刷新。</summary>
        internal static void RaiseRefreshed(int visibleCount)
        {
            var handler = CatalogRefreshed;
            if (handler == null) return;

            try { handler(visibleCount); }
            catch (Exception e)
            {
                if (ItemCatalog.ModEntry.Log != null)
                    ItemCatalog.ModEntry.Log.Warning("CatalogRefreshed handler failed: " + e.Message);
            }
        }

        /// <summary>
        /// 委托版变体注册（**反射友好**）：不引用本程序集的模组也能用。
        ///
        /// 用法（纯反射，无需 <c>using ProbablyEnoughItems.Api</c>）：
        /// <code>
        /// var api = /* 从已加载程序集里找到 ProbablyEnoughItems.Api.ItemCatalogApi */;
        /// var m = api.GetMethod("RegisterVariantProvider",
        ///     new[] { typeof(string), typeof(int), typeof(Func&lt;int, GameItem&gt;) });
        /// m.Invoke(null, new object[] { "wine_berry", 3, new Func&lt;int, GameItem&gt;(CreateVariant) });
        /// </code>
        /// 委托类型来自 Assembly-CSharp（GameItem），所以反射方不需要本程序集的类型。
        /// </summary>
        public static void RegisterVariantProvider(string itemId, int variantCount, Func<int, GameItem> factory)
        {
            RegisterVariantProvider(itemId, variantCount, factory, null);
        }

        /// <summary>
        /// 带标签的委托版变体注册，见上一个重载。
        /// 注意：<paramref name="labels"/> 目前不会被 UI 使用（图鉴不再在格子上写字），
        /// 参数保留只是为了不破坏已有调用方。
        /// </summary>
        public static void RegisterVariantProvider(string itemId, int variantCount,
            Func<int, GameItem> factory, Func<List<string>> labels)
        {
            if (string.IsNullOrEmpty(itemId)) throw new ArgumentException("itemId is required", "itemId");
            if (factory == null) throw new ArgumentNullException("factory");

            RegisterVariantProvider(new DelegateVariants(itemId, variantCount, factory, labels));
        }

        /// <summary>委托版额外条目注册（反射友好），见 <see cref="RegisterVariantProvider(string,int,Func{int,GameItem})"/>。</summary>
        public static void RegisterItemProvider(string modId, Func<List<GameItem>> factory)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            RegisterItemProvider(new DelegateItems(modId, factory));
        }

        private sealed class DelegateVariants : ICatalogVariantProvider
        {
            private readonly string _itemId;
            private readonly int _count;
            private readonly Func<int, GameItem> _factory;
            // 图鉴不再在格子上写字，标签保持注释状态；恢复时连同接口成员一起解开。
            // private readonly Func<List<string>> _labels;

            internal DelegateVariants(string itemId, int count, Func<int, GameItem> factory, Func<List<string>> labels)
            {
                _itemId = itemId;
                _count = count;
                _factory = factory;
                // _labels = labels;
            }

            public string ItemId { get { return _itemId; } }

            public int VariantCount { get { return _count; } }

            public GameItem CreateVariant(int index) { return _factory(index); }

            // public List<string> GetVariantLabels() { return _labels == null ? null : _labels(); }
        }

        private sealed class DelegateItems : ICatalogItemProvider
        {
            private readonly string _modId;
            private readonly Func<List<GameItem>> _factory;

            internal DelegateItems(string modId, Func<List<GameItem>> factory)
            {
                _modId = modId;
                _factory = factory;
            }

            public string ModId { get { return _modId; } }

            public List<GameItem> GetExtraItems() { return _factory(); }
        }

        /// <summary>已注册的「额外条目提供者」数量（供数据构建阶段判断是否需要重建）。</summary>
        internal static int ItemProviderCount
        {
            get { return _itemProviders.Count; }
        }

        /// <summary>
        /// 已注册的「变体提供者」数量。同样要参与重建判断：
        /// 首次 Build 之后才注册的变体提供者，如果不触发重建，
        /// FlatEntries 的缓存不会失效，声明的 VariantCount 会被忽略（格子数不重算）。
        /// </summary>
        internal static int VariantProviderCount
        {
            get { return _variantProviders.Count; }
        }

        /// <summary>按 id 查变体提供者，没有则返回 null。</summary>
        public static ICatalogVariantProvider FindVariantProvider(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;

            for (int i = 0; i < _variantProviders.Count; i++)
            {
                var provider = _variantProviders[i];
                if (provider != null && string.Equals(provider.ItemId, itemId, StringComparison.Ordinal))
                    return provider;
            }

            return null;
        }

        /// <summary>
        /// 查某件模块实际存在的占格形状（精灵名列表，顺序即图鉴展开顺序）。
        /// 图鉴内部据此算「形状数 × 每形状效果数」；提供者想复刻模块展开时用得到。
        /// 非模块物品返回空列表。
        /// </summary>
        public static List<string> GetModuleShapes(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return new List<string>();

            try { return ItemCatalog.UI.CatalogWindow.ModuleShapes(itemId); }
            catch { return new List<string>(); }
        }

        /// <summary>收集所有额外条目提供者给出的物品（供数据构建阶段调用）。</summary>
        internal static List<KeyValuePair<string, GameItem>> CollectExtraItems()
        {
            var result = new List<KeyValuePair<string, GameItem>>();

            for (int i = 0; i < _itemProviders.Count; i++)
            {
                var provider = _itemProviders[i];
                if (provider == null) continue;

                List<GameItem> items;
                try { items = provider.GetExtraItems(); }
                catch (Exception e)
                {
                    if (ItemCatalog.ModEntry.Log != null) ItemCatalog.ModEntry.Log.Warning("item provider '" + SafeModId(provider) + "' failed: " + e.Message);
                    continue;
                }

                if (items == null) continue;

                for (int k = 0; k < items.Count; k++)
                {
                    var item = items[k];
                    if (item == null) continue;

                    string id = null;
                    try { id = item.identifier; } catch { }
                    if (string.IsNullOrEmpty(id)) continue;

                    result.Add(new KeyValuePair<string, GameItem>(id, item));
                }
            }

            return result;
        }

        private static string SafeModId(ICatalogItemProvider provider)
        {
            try { return provider.ModId ?? "?"; }
            catch { return "?"; }
        }
    }

    /// <summary>
    /// 有限变体提供者：实现方可精确描述「这件物品一共有几种可能、第 index 种长什么样」。
    /// 优先级高于图鉴的自动探测（自动探测只能猜测，这里由模组自己声明）。
    /// </summary>
    public interface ICatalogVariantProvider
    {
        /// <summary>目标物品 id（与注册表 id 一致）。</summary>
        string ItemId { get; }

        /// <summary>变体数量。返回 0 表示未知，图鉴会退回自动探测。</summary>
        int VariantCount { get; }

        /// <summary>
        /// 生成第 index 个变体（0 &lt;= index &lt; VariantCount）。
        /// 必须是确定的：同一 index 每次调用应返回等价结果。返回 null 表示该索引不可用。
        /// </summary>
        GameItem CreateVariant(int index);

        // 图鉴不再在格子上写字（用户明确否决），这条成员暂时注释；
        // 恢复时把上面 DelegateVariants 里的对应字段/方法一并解开即可。
        // /// <summary>
        // /// 可选：给 UI 用的简短标签（例如「品相 2」「赝品」）。
        // /// 返回 null 或长度不足时图鉴忽略。
        // /// </summary>
        // List<string> GetVariantLabels();
    }

    /// <summary>
    /// 额外条目提供者：把注册表之外的物品放进图鉴。
    ///
    /// 两种用法：
    /// - <b>加载阶段就定好清单</b>：直接 <see cref="ItemCatalogApi.RegisterItemProvider"/>，
    ///   图鉴建表时会拉一次。
    /// - <b>运行中才确定</b>（例如按存档内容动态造物品）：先注册，之后清单变化时调用
    ///   <see cref="ItemCatalogApi.NotifyItemsChanged()"/>，图鉴会在下一个空闲帧重新拉取并增量补入，
    ///   不需要重启或重开图鉴。
    ///
    /// 注意：图鉴只“读”，不负责把这些物品注册进 <c>DirectoryMaster</c>。
    /// 要让游戏能按 id 重建（存档读回、拖拽落地依赖它），必须自己
    /// <c>DirectoryMaster.AddDirectory</c> + <c>Add(id, factory)</c>。
    /// </summary>
    public interface ICatalogItemProvider
    {
        /// <summary>来源模组 id，仅用于日志。</summary>
        string ModId { get; }

        /// <summary>要展示的物品列表。允许返回 null 或空列表。</summary>
        List<GameItem> GetExtraItems();
    }
}
