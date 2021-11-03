﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;

namespace JobBank.Scheduling
{
    /// <summary>
    /// Keyed collection of equally-weighted abstract queues that
    /// are automatically expired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class helps in implementing per-client queues to be
    /// made available by a server.  Clients can come and go at
    /// any time without explicit clean-up action, so this class
    /// can expire old inactive queues after a timeout.  Queues
    /// are associated to clients by their keys.
    /// </para>
    /// <para>
    /// All queues managed by an instance of this class are 
    /// equally weighted.  To effect prioritized scheduling,
    /// used this class in combination with 
    /// <see cref="PrioritizedQueueSystem{TMessage, TQueue}" />.
    /// </para>
    /// </remarks>
    /// <typeparam name="TMessage">
    /// The type of job or message being delivered by this queue system.
    /// </typeparam>
    /// <typeparam name="TKey">
    /// The key which is used to look up a queue, typically representing
    /// a client identity.
    /// </typeparam>
    /// <typeparam name="TQueue">
    /// The abstract queue instantiated for each priority class.
    /// </typeparam>
    public class ClientQueueSystem<TMessage, TKey, TQueue> 
        : ISchedulingFlow<TMessage>
        , IReadOnlyDictionary<TKey, TQueue>
        where TKey : notnull
        where TQueue : ISchedulingFlow<TMessage>
    {
        private struct Entry
        {
            public long DeactivationTime;
            public TQueue Queue;
            public uint Epoch;
            public bool IsInExpiryQueue;
            public bool IsNewlyAdded;
        }

        private readonly Func<TKey, TQueue> _factory;
        private readonly SchedulingGroup<TMessage> _schedulingGroup;
        private readonly Dictionary<TKey, Entry> _members;
        private readonly SimpleExpiryQueue _expiryQueue;

        public ClientQueueSystem(IEqualityComparer<TKey> comparer,
                                 Func<TKey, TQueue> factory,
                                 SimpleExpiryQueue expiryQueue)
        {
            _factory = factory;
            _schedulingGroup = new SchedulingGroup<TMessage>(capacity: 9,
                static (sender, args) =>
                {
                    var self = Unsafe.As<ClientQueueSystem<TMessage, TKey, TQueue>>(sender!);
                    self.OnSchedulingActivationEvent(args);
                }, this);
            _members = new(comparer);
            _expiryQueue = expiryQueue;
        }
        
        private void OnSchedulingActivationEvent(in SchedulingActivationEventArgs args)
        {
            var key = (TKey)args.Attachment!;

            bool toQueueForExpiry = false;
            var time = args.Activated ? long.MaxValue
                                      : Environment.TickCount64;

            lock (_members)
            {
                if (!_members.TryGetValue(key, out var entry))
                    return;

                // Ignore if events come in out of order
                if (!args.IsNewerThan(entry.Epoch) && !entry.IsNewlyAdded)
                    return;

                if (!args.Activated)
                {
                    toQueueForExpiry = !entry.IsInExpiryQueue;
                    entry.IsInExpiryQueue = true;
                }

                entry.IsNewlyAdded = false;
                entry.Epoch = args.Counter;
                entry.DeactivationTime = time;
                _members[key] = entry;
            }

            if (toQueueForExpiry)
                ScheduleForExpiry(args.Attachment!);
        }

        private void ScheduleForExpiry(object key)
            => _expiryQueue.Enqueue((now, state) => TryCleanUp(now, state!), key);

        private void TryCleanUp(long now, object state)
        {
            var key = (TKey)state;

            lock (_members)
            {
                if (!_members.TryGetValue(key, out var entry))
                    return;

                if (entry.DeactivationTime == long.MaxValue)
                {
                    entry.IsInExpiryQueue = false;
                    _members[key] = entry;
                    return;
                }

                if (entry.DeactivationTime < now - _expiryQueue.ExpiryTicks)
                {
                    // Expire now
                    _members.Remove(key);
                    return;
                }
            }

            ScheduleForExpiry(state);
        }

        public TQueue GetOrAdd(TKey key)
        {
            while (true)
            {
                Entry entry;
                bool exists;
                lock (_members)
                {
                    exists = _members.TryGetValue(key, out entry);
                }

                if (exists)
                    return entry.Queue;

                var queue = _factory(key);
                entry.Queue = queue;
                entry.DeactivationTime = Environment.TickCount64;
                entry.IsInExpiryQueue = true;
                entry.IsNewlyAdded = true;

                lock (_members)
                {
                    exists = !_members.TryAdd(key, entry);
                }

                if (!exists)
                {
                    object attachment = key;
                    _schedulingGroup.AdmitChild(queue.AsFlow(), false, attachment);
                    ScheduleForExpiry(attachment);
                    return queue;
                }
            }
        }

        public TQueue this[TKey key]
        {
            get
            {
                Entry entry;
                bool exists;
                lock (_members)
                {
                    exists = _members.TryGetValue(key, out entry);
                }

                if (!exists)
                    throw new KeyNotFoundException($"Member with the key {key} does not exist in this client queue system. ");

                return entry.Queue;
            }
        }

        /// <inheritdoc cref="ISchedulingFlow{T}.AsFlow" />
        public SchedulingFlow<TMessage> AsFlow() => _schedulingGroup.AsFlow();

        /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.Keys" />
        public IEnumerable<TKey> Keys => ListMembers().Select(item => item.Key);

        /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.Values" />
        public IEnumerable<TQueue> Values => ListMembers().Select(item => item.Value);

        /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.Count" />
        public int Count => _members.Count;

        /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.ContainsKey" />
        public bool ContainsKey(TKey key)
        {
            lock (_members)
            {
                return _members.ContainsKey(key);
            }
        }

        /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.TryGetValue" />
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TQueue value)
        {
            bool exists;
            Entry entry;
            lock (_members)
            {
                exists = _members.TryGetValue(key, out entry);
            }

            value = exists ? entry.Queue : default;
            return exists;
        }

        /// <summary>
        /// Take a snapshot of all the member queues currently in this queue system.
        /// </summary>
        public KeyValuePair<TKey, TQueue>[] ListMembers()
        {
            KeyValuePair<TKey, TQueue>[] items;

            lock (_members)
            {
                items = new KeyValuePair<TKey, TQueue>[_members.Count];

                int index = 0;
                foreach (var item in _members)
                    items[index++] = new(item.Key, item.Value.Queue);
            }

            return items;
        }

        /// <inheritdoc cref="IEnumerable{T}.GetEnumerator" />
        public IEnumerator<KeyValuePair<TKey, TQueue>> GetEnumerator()
            => ((IEnumerable<KeyValuePair<TKey, TQueue>>)ListMembers()).GetEnumerator();

        /// <inheritdoc cref="IEnumerable.GetEnumerator" />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
