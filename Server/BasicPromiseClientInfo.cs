﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// A basic implementation of <see cref="IPromiseClientInfo" />.
    /// </summary>
    public class BasicPromiseClientInfo : IPromiseClientInfo
    {
        private uint _subscriptionCount;

        public string UserName => "current-user";

        public void OnSubscribe(Subscription subscription, out uint index)
        {
            index = Interlocked.Increment(ref _subscriptionCount);
        }

        public void OnUnsubscribe(Subscription subscription)
        {
        }
    }
}
