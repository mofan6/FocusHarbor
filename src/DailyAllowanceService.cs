using System;
using System.Globalization;
using System.IO;

namespace FocusHarbor
{
    internal static class DailyAllowanceService
    {
        internal static bool IsForfeitedToday()
        {
            return IsForfeitedOn(DateTime.Today);
        }

        internal static bool IsForfeitedOn(DateTime day)
        {
            string date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            bool unreadable = false;
            foreach (string path in new[] { JsonStore.PauseForfeiturePath, JsonStore.PauseForfeitureBackupPath })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    DailyPauseForfeitureState state = JsonStore.Load<DailyPauseForfeitureState>(path);
                    DateTime parsed;
                    if (state == null || !DateTime.TryParseExact(state.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                    {
                        unreadable = true;
                        continue;
                    }
                    if (String.Equals(state.Date, date, StringComparison.Ordinal)) return true;
                }
                catch { unreadable = true; }
            }
            // A damaged forfeiture record must not silently recreate today's quota.
            return unreadable;
        }

        // Caller holds the temporary-pause mutex, also used by quota consumption.
        internal static void ForfeitToday()
        {
            DateTimeOffset now = DateTimeOffset.Now;
            DailyPauseForfeitureState state = new DailyPauseForfeitureState
            {
                Date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ForfeitedAt = now.ToString("o", CultureInfo.InvariantCulture)
            };
            JsonStore.SaveRedundant(JsonStore.PauseForfeiturePath, JsonStore.PauseForfeitureBackupPath, state);
        }
    }
}
