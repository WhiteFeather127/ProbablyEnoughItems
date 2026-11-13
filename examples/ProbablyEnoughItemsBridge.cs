using System;
using System.Collections.Generic;
using System.Reflection;

namespace ProbablyEnoughItems.Example
{
    /// <summary>
    /// 软依赖桥接：**不需要引用 ProbablyEnoughItems.dll，也不需要写 &lt;Prerequisites&gt;**。
    ///
    /// 用法：把这个文件拷进你自己的模组工程（改一下 namespace），然后
    /// <code>
    /// ProbablyEnoughItemsBridge.RegisterVariants("wine_berry", 3, CreateVariant);
    /// ProbablyEnoughItemsBridge.RegisterItems("my_mod", BuildExtras);
    /// </code>
    /// 图鉴没装时这两个调用只返回 false，不会抛异常、不会刷日志。
    ///
    /// 原理：从已加载程序集里按名字找到 Api 类型，用反射调用它的「委托版」重载。
    /// 委托类型是 Func&lt;int, GameItem&gt; / Func&lt;List&lt;GameItem&gt;&gt;，
    /// 只依赖 Assembly-CSharp，所以双方不共享任何自定义类型。
    /// </summary>
    internal static class ProbablyEnoughItemsBridge
    {
        private const string ApiTypeName = "ProbablyEnoughItems.Api.ItemCatalogApi";

        private static Type _api;
        private static bool _searched;

        /// <summary>图鉴是否可用（已加载且接口版本可识别）。</summary>
        internal static bool IsAvailable
        {
            get { return Resolve() != null; }
        }

        /// <summary>
        /// 注册有限变体：图鉴按 index 0..count-1 顺序播放。
        /// 返回 false 表示图鉴不在（或接口版本过旧），属正常情况，不必报错。
        /// </summary>
        internal static bool RegisterVariants(string itemId, int count, Func<int, GameItem> factory)
        {
            var api = Resolve();
            if (api == null) return false;

            var method = api.GetMethod("RegisterVariantProvider",
                new Type[] { typeof(string), typeof(int), typeof(Func<int, GameItem>) });

            if (method == null) return false;   // 旧版本没有委托版重载

            try
            {
                method.Invoke(null, new object[] { itemId, count, factory });
                return true;
            }
            catch (Exception e)
            {
                Warn("RegisterVariants('" + itemId + "')", e);
                return false;
            }
        }

        /// <summary>注册注册表之外的额外条目；返回 false 表示图鉴不在。</summary>
        internal static bool RegisterItems(string modId, Func<List<GameItem>> factory)
        {
            var api = Resolve();
            if (api == null) return false;

            var method = api.GetMethod("RegisterItemProvider",
                new Type[] { typeof(string), typeof(Func<List<GameItem>>) });

            if (method == null) return false;

            try
            {
                method.Invoke(null, new object[] { modId, factory });
                return true;
            }
            catch (Exception e)
            {
                Warn("RegisterItems('" + modId + "')", e);
                return false;
            }
        }

        /// <summary>通知图鉴物品清单变了（运行中新增物品时用）；返回 false 表示图鉴不在。</summary>
        internal static bool NotifyItemsChanged(string reason)
        {
            var api = Resolve();
            if (api == null) return false;

            var method = api.GetMethod("NotifyItemsChanged", new Type[] { typeof(string) });
            if (method == null) return false;

            try
            {
                method.Invoke(null, new object[] { reason });
                return true;
            }
            catch (Exception e)
            {
                Warn("NotifyItemsChanged", e);
                return false;
            }
        }

        /// <summary>找 Api 类型；找不到时下次调用会重新尝试（图鉴可能比我们晚加载）。</summary>
        private static Type Resolve()
        {
            if (_searched) return _api;

            _searched = true;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    var type = assemblies[i].GetType(ApiTypeName, false);
                    if (type != null)
                    {
                        _api = type;
                        return _api;
                    }
                }
                catch { }
            }

            _searched = false;   // 允许下次重试
            return null;
        }

        private static void Warn(string what, Exception e)
        {
            var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            UnityEngine.Debug.LogWarning("[ProbablyEnoughItemsBridge] " + what + " failed: " + inner.GetType().Name + ": " + inner.Message);
        }
    }
}
