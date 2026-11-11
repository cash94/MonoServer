// Сборщик мусора на сегментах вместо регионов (Linux, однофайловая сборка).
//
// GC .NET 8 на регионах (по умолчанию) после просмотра держал закоммиченными
// 70 МБ при живой куче 2 МБ и не отдавал их даже «агрессивной» сборкой; прежний
// GC на сегментах (libclrgc.so — входит в сам .NET 8) — 15 МБ: процесс в простое
// 114 МБ против 171 (замер 1.0.36: Sintel 123 МБ потоком, закрытие, 2,5 мин).
// Маленькие регионы (GCRegionSize 1 МБ, 512 КБ) — только хуже: 190–200 МБ.
//
// Выбрать GC можно только до запуска рантайма — переменной DOTNET_GCName, и файл
// GC должен лежать рядом с программой (не в бандле). Вшивать в конфиг сборки
// нельзя: без файла служба не стартовала бы вовсе, а monoserver.sh update прежних
// версий из архива берёт только MonoServer, и unit systemd не переписывает. Поэтому
// сама программа: файл — из своих ресурсов рядом с собой (каталог не записать —
// без него), и execve себя же с DOTNET_GCName. PID тот же — systemd не замечает.
// Что-то не так — работаем дальше на регионах, как раньше.
// MONOSERVER_GC=regions — не переключать.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TorrStreamTorrServer
{
    static class GcSwitch
    {
        const string FileName = "libclrgc.so";
        const string Resource = "clrgc";

        [DllImport("libc", SetLastError = true)]
        static extern int execve(string path, string[] argv, string[] envp);

        /// <summary>Самым первым в Main: перезапустить себя с GC на сегментах; вернулся — остаёмся как есть</summary>
        public static void Apply(string[] args)
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_GCName"))) return;   // уже (или задано снаружи)
                if (Environment.GetEnvironmentVariable("MONOSERVER_GC") == "regions") return;
                // Только однофайловая сборка: под `dotnet MonoServer.dll` программа — это dotnet
                if (!string.IsNullOrEmpty(typeof(GcSwitch).Assembly.Location)) return;
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
                string gc = Path.Combine(Path.GetDirectoryName(exe), FileName);
                if (!Ensure(gc)) return;

                var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                    .Select(kv => kv.Key + "=" + kv.Value).ToList();
                env.Add("DOTNET_GCName=" + FileName);
                var argv = new[] { exe }.Concat(args).Concat(new string[] { null }).ToArray();
                Console.Out.Flush();
                execve(exe, argv, env.Concat(new string[] { null }).ToArray());
                // Вернулся — execve не удался: работаем как есть
                Console.Error.WriteLine("GC на сегментах не включён: execve, ошибка " + Marshal.GetLastWin32Error());
            }
            catch (Exception e) { Console.Error.WriteLine("GC на сегментах не включён: " + e.Message); }
        }

        // Файл GC рядом с программой — ровно тот, что вшит (той же версии рантайма)
        static bool Ensure(string path)
        {
            byte[] want;
            using (var s = typeof(GcSwitch).Assembly.GetManifestResourceStream(Resource))
            {
                if (s == null) return File.Exists(path);   // сборка без ресурса — если файл положили руками
                using (var m = new MemoryStream()) { s.CopyTo(m); want = m.ToArray(); }
            }
            if (File.Exists(path) && Same(File.ReadAllBytes(path), want)) return true;
            try
            {
                string tmp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(tmp, want);
                File.Move(tmp, path, true);
                return Same(File.ReadAllBytes(path), want);
            }
            catch { return false; }   // каталог только для чтения — на регионах
        }

        static bool Same(byte[] a, byte[] b)
        {
            using (var h = SHA256.Create())
                return a.Length == b.Length && h.ComputeHash(a).SequenceEqual(h.ComputeHash(b));
        }
    }
}
