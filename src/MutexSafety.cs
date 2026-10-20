using System;
using System.Threading;

namespace FocusHarbor
{
    internal static class MutexSafety
    {
        public const string ConfigMutexName = @"Local\FocusHarborConfiguration";

        public static bool Wait(Mutex mutex, TimeSpan timeout)
        {
            try
            {
                return mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership before reporting an abandoned mutex.
                // Treat it as acquired so the caller can repair state and release it.
                return true;
            }
        }

        public static bool Wait(Mutex mutex, int millisecondsTimeout)
        {
            try
            {
                return mutex.WaitOne(millisecondsTimeout, false);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
    }
}
