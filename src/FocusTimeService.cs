using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FocusHarbor
{
    public static class FocusTimeService
    {
        public static long Update(bool focusing, bool forceSave)
        {
            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborFocusTime"))
            {
                bool lockTaken = false;
                try
                {
                    lockTaken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(3));
                    if (!lockTaken)
                    {
                        return ReadTotalWithoutUpdate();
                    }

                    DateTimeOffset now = DateTimeOffset.Now;
                    FocusTimeState state = LoadState(now);
                    DateTimeOffset last;
                    if (!DateTimeOffset.TryParse(state.LastAccountedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last) || last > now)
                    {
                        last = now;
                    }
                    long elapsedSeconds = Math.Max(0, (long)Math.Floor((now - last).TotalSeconds));
                    long currentTotal = Math.Max(0, state.TotalSeconds) + (state.WasFocusing ? elapsedSeconds : 0);
                    bool stateChanged = state.WasFocusing != focusing;

                    if (forceSave || stateChanged || elapsedSeconds >= 10 || !File.Exists(JsonStore.FocusTimePath))
                    {
                        state.Version = 1;
                        state.TotalSeconds = currentTotal;
                        state.LastAccountedAt = now.ToString("o", CultureInfo.InvariantCulture);
                        state.WasFocusing = focusing;
                        JsonStore.Save(JsonStore.FocusTimePath, state);
                    }
                    return currentTotal;
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Focus time accounting failed: " + exception.Message);
                    return ReadTotalWithoutUpdate();
                }
                finally
                {
                    if (lockTaken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        private static FocusTimeState LoadState(DateTimeOffset now)
        {
            try
            {
                if (File.Exists(JsonStore.FocusTimePath))
                {
                    FocusTimeState state = JsonStore.Load<FocusTimeState>(JsonStore.FocusTimePath);
                    if (state != null && state.TotalSeconds >= 0)
                    {
                        return state;
                    }
                }
            }
            catch
            {
            }
            return new FocusTimeState
            {
                Version = 1,
                TotalSeconds = 0,
                LastAccountedAt = now.ToString("o", CultureInfo.InvariantCulture),
                WasFocusing = false
            };
        }

        private static long ReadTotalWithoutUpdate()
        {
            try
            {
                if (File.Exists(JsonStore.FocusTimePath))
                {
                    FocusTimeState state = JsonStore.Load<FocusTimeState>(JsonStore.FocusTimePath);
                    return state == null ? 0 : Math.Max(0, state.TotalSeconds);
                }
            }
            catch
            {
            }
            return 0;
        }
    }
}
