using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;

namespace SysWidget
{
    /// <summary>
    /// Состояние физических дисков по данным хранилища Windows (MSFT_PhysicalDisk.HealthStatus).
    /// Читается без прав администратора. Температура и износ (StorageReliabilityCounter)
    /// администраторского доступа требуют, поэтому их здесь нет.
    /// Кэш на 10 минут: состояние диска не меняется по секундам, а WMI не бесплатен.
    /// </summary>
    public static class DiskHealth
    {
        static readonly object Gate = new object();
        static readonly Dictionary<int, string> Map = new Dictionary<int, string>();
        static DateTime _at = DateTime.MinValue;
        static bool _busy;

        /// <summary>Текст состояния диска («исправен», «предупреждение»…) или пусто, если неизвестно.</summary>
        public static string Get(int index)
        {
            lock (Gate)
            {
                string s;
                return Map.TryGetValue(index, out s) ? s : "";
            }
        }

        /// <summary>Обновить в фоне, если данные старше 10 минут. done вызывается в потоке пула.</summary>
        public static void Ensure(Action done)
        {
            lock (Gate)
            {
                if (_busy || (DateTime.Now - _at).TotalMinutes < 10) return;
                _busy = true;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                var fresh = new Dictionary<int, string>();
                try
                {
                    var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                    scope.Connect();
                    using (var q = new ManagementObjectSearcher(scope, new ObjectQuery(
                        "SELECT DeviceId,HealthStatus FROM MSFT_PhysicalDisk")))
                    using (var found = q.Get())
                    {
                        foreach (ManagementBaseObject o in found)
                        {
                            int idx;
                            if (!int.TryParse(Convert.ToString(o["DeviceId"], CultureInfo.InvariantCulture),
                                              NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                                continue;
                            int hs = 5;
                            try { hs = Convert.ToInt32(o["HealthStatus"], CultureInfo.InvariantCulture); } catch { }
                            fresh[idx] = Text(hs);
                        }
                    }
                }
                catch (Exception ex) { ErrorLog.Write("disk health", ex); }

                lock (Gate)
                {
                    if (fresh.Count > 0)
                    {
                        Map.Clear();
                        foreach (KeyValuePair<int, string> kv in fresh) Map[kv.Key] = kv.Value;
                    }
                    _at = DateTime.Now;     // и при неудаче — не долбим WMI каждые полторы секунды
                    _busy = false;
                }
                if (done != null) done();
            });
        }

        static string Text(int health)
        {
            switch (health)
            {
                case 0: return Lang.S("исправен", "healthy");
                case 1: return Lang.S("предупреждение", "warning");
                case 2: return Lang.S("неисправен", "unhealthy");
                default: return "";
            }
        }

        /// <summary>Состояние требует внимания (не «исправен» и не «неизвестно»).</summary>
        public static bool NeedsAttention(int index)
        {
            string s = Get(index);
            return s.Length > 0 && s != Lang.S("исправен", "healthy");
        }
    }
}
