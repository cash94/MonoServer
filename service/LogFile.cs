// Журнал службы в файле — переживает падение и перезапуск. На телевизоре журнала
// системы (dlog) часто не снять, а журнал в памяти (LogRing, /log) пропадает
// вместе с процессом — и после «служба пропала» смотреть было нечего.
// Файлы в каталоге данных, log/:
//   current.log (+ current.1.log, когда перевалил за MaxBytes) — этот запуск;
//   previous.log (+ previous.1.log) — прошлый, отдаёт GET /log?prev.
// Пишет пачкой раз в FlushMs (запись на флеш ТВ — не на каждую строку); при
// необработанной ошибке и штатной остановке — сразу (Host). Убитый системой
// процесс (нехватка памяти) теряет только последние FlushMs.
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TorrStreamTorrServer
{
    public class LogFile
    {
        public const long MaxBytes = 1L << 20;
        const int FlushMs = 2000;
        /// <summary>Последняя строка штатной остановки — по ней видно, что прошлый запуск не оборвался</summary>
        public const string StopLine = "служба остановлена";

        readonly string current, currentOld, previous, previousOld;
        readonly StringBuilder pending = new StringBuilder();
        readonly object writeLock = new object();
        Timer timer;
        bool broken;

        /// <summary>
        /// Чем кончился прошлый запуск: null — журнала нет (первый запуск),
        /// "stopped" — остановлен штатно, "crashed" — необработанная ошибка,
        /// "killed" — оборвался без записи (закрыла система, нехватка памяти, выключение питания)
        /// </summary>
        public string PreviousRun { get; private set; }
        /// <summary>Время последней строки прошлого запуска (как в файле) или null</summary>
        public string PreviousLast { get; private set; }

        public LogFile(string dataDir)
        {
            string dir = Path.Combine(dataDir, "log");
            current = Path.Combine(dir, "current.log");
            currentOld = Path.Combine(dir, "current.1.log");
            previous = Path.Combine(dir, "previous.log");
            previousOld = Path.Combine(dir, "previous.1.log");
            try
            {
                Directory.CreateDirectory(dir);
                // Этот запуск начинается — журнал прошлого в previous
                Replace(current, previous);
                Replace(currentOld, previousOld);
                Inspect();
            }
            catch { broken = true; }
            timer = new Timer(_ => Flush(), null, FlushMs, FlushMs);
        }

        static void Replace(string from, string to)
        {
            if (File.Exists(to)) File.Delete(to);
            if (File.Exists(from)) File.Move(from, to);
        }

        // Прошлый запуск по хвосту previous.log: последняя строка — штатная остановка?
        void Inspect()
        {
            string path = File.Exists(previous) ? previous : (File.Exists(previousOld) ? previousOld : null);
            if (path == null) return;
            string tail = Tail(path, 16384);
            var lines = tail.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            string last = lines[lines.Length - 1];
            // Время — последней строки со временем: у ошибки следом идут строки стека
            for (int i = lines.Length - 1; i >= 0 && PreviousLast == null; i--)
                if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d ")) PreviousLast = lines[i].Substring(0, 19);
            if (last.Contains(StopLine)) PreviousRun = "stopped";
            else if (tail.Contains("необработанная ошибка:")) PreviousRun = "crashed";
            else PreviousRun = "killed";
        }

        static string Tail(string path, int bytes)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long from = Math.Max(0, f.Length - bytes);
                f.Seek(from, SeekOrigin.Begin);
                var buf = new byte[f.Length - from];
                int n = 0, r;
                while (n < buf.Length && (r = f.Read(buf, n, buf.Length - n)) > 0) n += r;
                return Encoding.UTF8.GetString(buf, 0, n);
            }
        }

        public void Add(string line)
        {
            if (broken) return;
            lock (pending)
            {
                // Не пишет (флеш полон и т. п.) — не копить в памяти без конца
                if (pending.Length > MaxBytes) return;
                pending.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ")).Append(line).Append('\n');
            }
        }

        /// <summary>Записать накопленное сейчас (необработанная ошибка, остановка)</summary>
        public void Flush()
        {
            if (broken) return;
            lock (writeLock)
            {
                string text;
                lock (pending)
                {
                    if (pending.Length == 0) return;
                    text = pending.ToString();
                    pending.Clear();
                }
                try
                {
                    var fi = new FileInfo(current);
                    if (fi.Exists && fi.Length > MaxBytes) Replace(current, currentOld);
                    File.AppendAllText(current, text, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        /// <summary>Журнал прошлого запуска целиком (до 2 × MaxBytes) или null</summary>
        public string Previous()
        {
            var sb = new StringBuilder();
            foreach (var p in new[] { previousOld, previous })
                try { if (File.Exists(p)) sb.Append(File.ReadAllText(p, Encoding.UTF8)); } catch { }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public void Close()
        {
            try { timer?.Dispose(); } catch { }
            Flush();
        }
    }
}
