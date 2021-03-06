﻿using System;
using System.Linq;
using UIKit;


namespace Plugin.Jobs
{
    public static partial class CrossJobs
    {
        public static void Init()
        {
            Current = new JobManagerImpl();
            UIApplication.SharedApplication.SetMinimumBackgroundFetchInterval(UIApplication.BackgroundFetchIntervalMinimum);
        }


        public static void OnBackgroundFetch(Action<UIBackgroundFetchResult> completionHandler)
            => Current.RunTask("BackgroundTask", async () =>
            {
                var results = await Current.RunAll().ConfigureAwait(false);
                if (!results.Any())
                    completionHandler(UIBackgroundFetchResult.NoData);
                else if (results.Any(x => !x.Success))
                    completionHandler(UIBackgroundFetchResult.Failed);
                else
                    completionHandler(UIBackgroundFetchResult.NewData);
            });
    }
}
