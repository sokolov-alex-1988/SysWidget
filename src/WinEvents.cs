using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading;

namespace SysWidget
{
    /// <summary>Одно событие журнала Windows, отобранное для диагностики.</summary>
    public sealed class WinEvent
    {
        public DateTime Time;
        public int Cat;             // WinEvents.Cat*
        public string Title = "";   // по нему одинаковые события сворачиваются в «×N»
        public string Detail = "";
    }

    /// <summary>
    /// «События Windows»: сбои приложений, перезагрузки без корректного завершения и синие экраны,
    /// ошибки диска, аварии служб, аппаратные ошибки (WHEA) и сброс драйвера видеокарты — за 7 суток.
    /// Читаются журналы «Приложение» и «Система»: они доступны обычному пользователю (журнал
    /// «Безопасность» нужен администратору и не используется). Только чтение, ничего не пишется.
    /// </summary>
    public static class WinEvents
    {
        public const int CatApp = 0, CatReboot = 1, CatDisk = 2, CatService = 3, CatHardware = 4;
        public const int Days = 7;

        public static List<WinEvent> Items = new List<WinEvent>();
        public static DateTime Checked = DateTime.MinValue;
        public static string Error = "";
        public static volatile bool Busy;

        public static void Run(Action done)
        {
            if (Busy) return;
            Busy = true;
            Thread t = new Thread((ThreadStart)delegate
            {
                try
                {
                    var list = new List<WinEvent>();
                    string err = "";
                    long ms = (long)Days * 24 * 3600 * 1000;
                    string time = "TimeCreated[timediff(@SystemTime) <= " + ms + "]";

                    Read("Application", "*[System[(EventID=1000 or EventID=1002) and " + time + "]]", list, ref err);
                    Read("System", "*[System[(EventID=41 or EventID=1001 or EventID=7 or EventID=11 or EventID=51 or EventID=55 " +
                        "or EventID=153 or EventID=17 or EventID=18 or EventID=19 or EventID=20 or EventID=4101 " +
                        "or EventID=7031 or EventID=7034 or EventID=7000 or EventID=7001 or EventID=7009 or EventID=7011 or EventID=7023) and " +
                        time + "]]", list, ref err);

                    list.Sort(delegate(WinEvent a, WinEvent b) { return b.Time.CompareTo(a.Time); });
                    Items = list;
                    Error = err;
                    Checked = DateTime.Now;
                }
                catch (Exception ex) { ErrorLog.Write("win events", ex); Error = ex.Message; }
                finally { Busy = false; }
                if (done != null) { try { done(); } catch { } }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        static void Read(string log, string query, List<WinEvent> list, ref string error)
        {
            try
            {
                var q = new EventLogQuery(log, PathType.LogName, query);
                using (var r = new EventLogReader(q))
                {
                    EventRecord e;
                    while ((e = r.ReadEvent()) != null)
                    {
                        using (e) { try { Handle(e, list); } catch (Exception ex) { ErrorLog.Write("win event item", ex); } }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                error = Lang.S("нет доступа к журналу «" + log + "»", "no access to the “" + log + "” log");
            }
            catch (EventLogNotFoundException) { }
            catch (Exception ex)
            {
                ErrorLog.Write("win events read " + log, ex);
                error = ex.Message;
            }
        }

        static string P(EventRecord e, int i)
        {
            try
            {
                if (e.Properties == null || i >= e.Properties.Count) return "";
                object v = e.Properties[i].Value;
                return v == null ? "" : Convert.ToString(v).Trim();
            }
            catch { return ""; }
        }

        /// <summary>Первая строка описания события, не длиннее n символов.</summary>
        static string Desc(EventRecord e, int n)
        {
            try
            {
                string d = e.FormatDescription();
                if (string.IsNullOrEmpty(d)) return "";
                int nl = d.IndexOfAny(new char[] { '\r', '\n' });
                if (nl > 0) d = d.Substring(0, nl);
                d = d.Trim();
                return d.Length > n ? d.Substring(0, n - 1) + "…" : d;
            }
            catch { return ""; }
        }

        static void Handle(EventRecord e, List<WinEvent> list)
        {
            string prov = e.ProviderName ?? "";
            int id = e.Id;
            DateTime time = e.TimeCreated ?? DateTime.MinValue;
            var ev = new WinEvent();
            ev.Time = time;

            if (prov == "Application Error" && id == 1000)
            {
                ev.Cat = CatApp;
                ev.Title = P(e, 0);
                string mod = P(e, 3);
                ev.Detail = mod.Length > 0 ? Lang.S("сбой в модуле ", "crash in ") + mod : Lang.S("аварийное завершение", "crashed");
            }
            else if (prov == "Application Hang" && id == 1002)
            {
                ev.Cat = CatApp;
                ev.Title = P(e, 0);
                ev.Detail = Lang.S("зависло и было закрыто", "stopped responding");
            }
            else if (prov == "Microsoft-Windows-Kernel-Power" && id == 41)
            {
                ev.Cat = CatReboot;
                ev.Title = Lang.S("Перезагрузка без корректного завершения", "Restart without a clean shutdown");
                long code = 0;
                long.TryParse(P(e, 0), out code);
                ev.Detail = code != 0 ? Lang.S("код остановки 0x", "stop code 0x") + code.ToString("X")
                                      : Lang.S("сбой питания, зависание или сброс кнопкой", "power loss, freeze or hard reset");
            }
            else if (prov == "Microsoft-Windows-WER-SystemErrorReporting" && id == 1001)
            {
                ev.Cat = CatReboot;
                ev.Title = Lang.S("Синий экран (BSOD)", "Blue screen (BSOD)");
                string c = P(e, 0);
                int sp = c.IndexOf(' ');
                ev.Detail = Lang.S("код ", "code ") + (sp > 0 ? c.Substring(0, sp) : c);
            }
            else if ((prov == "disk" && (id == 7 || id == 11 || id == 51 || id == 153)) || (prov == "Ntfs" && id == 55))
            {
                ev.Cat = CatDisk;
                switch (id)
                {
                    case 7: ev.Title = Lang.S("Плохой блок на диске", "Bad block on the disk"); break;
                    case 11: ev.Title = Lang.S("Ошибка контроллера диска", "Disk controller error"); break;
                    case 51: ev.Title = Lang.S("Ошибка ввода-вывода диска", "Disk I/O error"); break;
                    case 153: ev.Title = Lang.S("Повтор операции ввода-вывода диска", "Disk I/O retried"); break;
                    default: ev.Title = Lang.S("Повреждение файловой системы (NTFS)", "File system corruption (NTFS)"); break;
                }
                ev.Detail = Desc(e, 110);
            }
            else if (prov == "Service Control Manager" &&
                     (id == 7031 || id == 7034 || id == 7000 || id == 7001 || id == 7009 || id == 7011 || id == 7023))
            {
                ev.Cat = CatService;
                ev.Title = (id == 7009 || id == 7011) ? P(e, 1) : P(e, 0);      // у 7009/7011 первое поле — таймаут, мс
                if (id == 7031 || id == 7034) ev.Detail = Lang.S("служба аварийно завершилась", "service terminated unexpectedly");
                else if (id == 7009 || id == 7011) ev.Detail = Lang.S("служба не ответила вовремя", "service did not respond in time");
                else ev.Detail = Lang.S("служба не запустилась", "service failed to start");
            }
            else if (prov == "Microsoft-Windows-WHEA-Logger" && id >= 17 && id <= 20)
            {
                ev.Cat = CatHardware;
                ev.Title = Lang.S("Аппаратная ошибка (WHEA)", "Hardware error (WHEA)");
                ev.Detail = Desc(e, 110);
            }
            else if (prov == "Display" && id == 4101)
            {
                ev.Cat = CatHardware;
                ev.Title = Lang.S("Драйвер видеокарты перезапущен", "Display driver was reset");
                ev.Detail = Lang.S("видеодрайвер перестал отвечать (TDR)", "the display driver stopped responding (TDR)");
            }
            else return;

            if (ev.Title.Length == 0) ev.Title = prov + " " + id;
            list.Add(ev);
        }
    }
}
