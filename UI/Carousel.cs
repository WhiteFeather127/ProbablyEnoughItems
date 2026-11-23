using System;
using System.Collections.Generic;

namespace ItemCatalog.UI
{
    /// <summary>
    /// 图鉴里所有「每秒换一件」的轮播共用这一个对象：具名游标（一个键一条）+
    /// 定长槽位游标（枪械改装的 8 个槽各自一条）。各处不再自己维护
    /// int / int[] / Dictionary，一律通过 <see cref="Current"/> 读写。
    ///
    /// 具名键的约定：
    ///   "portrait"          客人立绘
    ///   "machine"           机器页档内配方
    ///   "tag:&lt;tag&gt;"        用途页标签格
    ///   "cell:&lt;格子 id&gt;"    货单里容器图标
    ///   "prebuilt:&lt;物品 id&gt;" 预构建物品的外观轮换
    ///
    /// 所有索引都按调用方给的集合长度取模，调用方不用自己处理越界 / 负数。
    /// </summary>
    internal sealed class Carousel
    {
        /// <summary>全局唯一实例。</summary>
        internal static Carousel Current { get; } = new Carousel();

        private readonly Dictionary<string, int> _cursors =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private int[] _slots = new int[0];

        private Carousel() { }

        /// <summary>具名游标的条数（诊断用）。</summary>
        internal int Count { get { return _cursors.Count; } }

        /// <summary>读游标当前落点（按 count 取模）；没有该键时停在 0。</summary>
        internal int Position(string key, int count)
        {
            if (count <= 0) return 0;

            int cursor;
            _cursors.TryGetValue(key, out cursor);
            return ((cursor % count) + count) % count;
        }

        /// <summary>前进一格，返回前进后的落点（按 count 取模）。</summary>
        internal int Advance(string key, int count)
        {
            if (count <= 0) return 0;

            int cursor;
            _cursors.TryGetValue(key, out cursor);
            _cursors[key] = cursor + 1;
            return (((cursor + 1) % count) + count) % count;
        }

        /// <summary>直接把游标定在某个值上（例如立绘按客人名错开起点）。</summary>
        internal void Set(string key, int value)
        {
            _cursors[key] = value;
        }

        internal void Remove(string key)
        {
            _cursors.Remove(key);
        }

        /// <summary>清空全部游标（关窗时调用，下次打开从头开始）。</summary>
        internal void Clear()
        {
            _cursors.Clear();
            _slots = new int[0];
        }

        // ---------------------------------------------------------------- 槽位游标

        internal int SlotCount { get { return _slots.Length; } }

        /// <summary>按槽数重建槽位游标；槽数没变时保留各自的位置。</summary>
        internal void ResizeSlots(int count)
        {
            if (count < 0) count = 0;
            if (_slots.Length == count) return;

            _slots = new int[count];
        }

        /// <summary>按槽数重建槽位游标并把每个槽归零（进入新的一页时用）。</summary>
        internal void ResetSlots(int count)
        {
            if (count < 0) count = 0;
            _slots = new int[count];
        }

        /// <summary>读某个槽位的落点（按 count 取模）。</summary>
        internal int Slot(int index, int count)
        {
            if (count <= 0 || index < 0 || index >= _slots.Length) return 0;

            int cursor = _slots[index];
            return ((cursor % count) + count) % count;
        }

        /// <summary>某个槽位前进一格，返回前进后的落点。</summary>
        internal int AdvanceSlot(int index, int count)
        {
            if (count <= 0 || index < 0 || index >= _slots.Length) return 0;

            int next = _slots[index] + 1;
            _slots[index] = next;
            return ((next % count) + count) % count;
        }
    }
}
