﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Hearty.Scheduling;
using Hearty.Utilities;

namespace Hearty.Server;

using JobMessage = ILaunchableJob<PromisedWork, PromiseData>;
using MacroJobExpansion = IAsyncEnumerable<(PromiseRetriever, PromisedWork)>;

using MacroJobLinks = CircularListLinks<MacroJobMessage, MacroJobMessage.ListLinksAccessor>;

/// <summary>
/// The message type put into the queues managed by
/// <see cref="JobsManager" /> that implements
/// a "macro job".
/// </summary>
/// <remarks>
/// <para>
/// A "macro job" expands into many "micro jobs" only when
/// the macro job is de-queued.  This feature avoids 
/// having to push hundreds and thousands of messages 
/// (for the micro jobs) into the queue which makes it
/// hard to visualize and manage.  Resources can also
/// be conserved if the generator of the micro jobs
/// is able to, internally, represent micro jobs more 
/// efficiently than generic messages in the job
/// scheduling queues.
/// </para>
/// <para>
/// A user-supplied generator
/// lists out the micro jobs as <see cref="PromisedWork" />
/// descriptors, and this class transforms them into
/// the messages that are put into the job queue,
/// to implement job sharing and time accounting.
/// </para>
/// </remarks>
internal sealed class MacroJobMessage : IAsyncEnumerable<JobMessage>
                                      , IPromisedWorkInfo
                                      , IJobCancellation
                                      , IDisposable
{
    /// <summary>
    /// The client queue that micro jobs generated by this instance
    /// shall be pushed into.
    /// </summary>
    private readonly ClientJobQueue _queue;

    /// <summary>
    /// Exposes <see cref="_listLinks" /> for linked-list operations.
    /// </summary>
    internal struct ListLinksAccessor : IInteriorStruct<MacroJobMessage, MacroJobLinks>
    {
        public ref MacroJobLinks GetInteriorReference(MacroJobMessage target)
            => ref target._listLinks;
    }

    /// <summary>
    /// Has this instance participate in a linked list of 
    /// other instances referring to the same macro job.
    /// </summary>
    private MacroJobLinks _listLinks;

    /// <summary>
    /// Allow <see cref="MacroJob" /> to walk through its list
    /// of participants.
    /// </summary>
    internal ref readonly MacroJobLinks ListLinks => ref _listLinks;

    /// <summary>
    /// The macro job that may be shared with other instances of this class.
    /// </summary>
    public MacroJob Source { get; }

    /// <summary>
    /// Provides the cancellation token for all micro jobs
    /// spawned from this macro job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This token allows the micro jobs to be cancelled along
    /// with this macro job when only this macro job is to be
    /// cancelled, even if the cancellation token passed externally
    /// is linked to other operations.
    /// </para>
    /// <para>
    /// The cancellation source is rented only when the macro job
    /// starts, for efficiency, but also to enable cancellation
    /// to be triggered in the background, and avoid having
    /// to dispose it if an instance of this class needs to be
    /// speculatively created.
    /// </para>
    /// </remarks>
    private CancellationSourcePool.Use _rentedCancellationSource;

    /// <summary>
    /// Set to true if this job has been requested to cancel.
    /// </summary>
    private bool _isCancelled;

    /// <summary>
    /// Cancellation token passed in from the client on construction.
    /// </summary>
    public CancellationToken ClientToken { get; }

    /// <summary>
    /// Links the cancellation token passed in the constructor
    /// into <see cref="_rentedCancellationSource" />.
    /// </summary>
    private CancellationTokenRegistration _clientCancellationRegistration;

    /// <summary>
    /// Set to non-zero when this instance is no longer valid
    /// as a job message. 
    /// </summary>
    /// <remarks>
    /// <para>
    /// This variable is flagged to 1 as soon as 
    /// <see cref="GetAsyncEnumerator" />
    /// is called, to disallow multiple calls.
    /// </para>
    /// <para>
    /// Enumeration of micro jobs can only be done once per instance
    /// of this class, because the jobs involve registrations that have
    /// to be accessed outside of the enumerator and cleaned up after
    /// the jobs finish executing.  Obviously, these registrations
    /// cannot be scoped to the enumerator instance.
    /// </para>
    /// <para>
    /// This variable is also flagged to -1
    /// when this instance should not even start enumerating jobs
    /// because it has been cancelled or disposed.
    /// </para>
    /// </remarks>
    private int _isInvalid;

    /// <summary>
    /// True when this instance is valid to enumerate (once),
    /// thus starting the job (when it is de-queued from the job queue).
    /// </summary>
    public bool IsValid => (_isInvalid == 0);

    /// <summary>
    /// Data that is reported through <see cref="IPromisedWorkInfo" />;
    /// it is not involved in creating or scheduling the jobs at all.
    /// </summary>
    private readonly PromisedWork _work;

    string? IPromisedWorkInfo.Route => _work.Route;

    string? IPromisedWorkInfo.Path => _work.Path;

    PromiseId? IPromisedWorkInfo.PromiseId => _work.Promise?.Id;

    int IPromisedWorkInfo.InitialWait => _work.InitialWait;

    /// <summary>
    /// If true, the client's request needs to be unregistered 
    /// from <see cref="JobsManager" /> when the macro
    /// job finishes.
    /// </summary>
    private bool _isTrackingClientRequest;

    /// <summary>
    /// Set this macro job to register the request so it
    /// can be de-duplicated and cancelled for remote clients.
    /// </summary>
    /// <remarks>
    /// Owing to certain technical reasons in the correct
    /// implementation of <see cref="JobsManager" />,
    /// the client tracking cannot be enabled until this instance 
    /// has been constructed, so enabling this feature can race
    /// with the macro job being killed, and so the caller
    /// must be prepared to back out.
    /// </remarks>
    /// <returns>
    /// True if this macro job has been successfully set to
    /// track the client request; false if it has already
    /// started running or has been cancelled.
    /// </returns>
    public bool TryTrackClientRequest()
    {
        var jobsManager = Source.JobsManager;
        var promiseId = Source.PromiseId;

        if (!jobsManager.TryRegisterClientRequest(promiseId, ClientToken, this))
            return false;

        _isTrackingClientRequest = true;

        // If this macro job gets killed concurrently, it
        // might still see _isTrackingClientRequest being
        // false, and not unregister itself as we expect.
        // Deal with this rarely occurring race by backing
        // out of the registration immediately.
        //
        // We intend to test _isInvalid if non-zero but on
        // weakly-ordered memory architectures an 
        // acquire+release barrier is required for correctness.
        if (Interlocked.CompareExchange(ref _isInvalid, 0, 0) != 0)
        {
            jobsManager.UnregisterClientRequest(promiseId, ClientToken);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Construct the macro job message.
    /// </summary>
    /// <param name="work">
    /// Information about the work.  This is only used
    /// for reporting.
    /// </param>
    /// <param name="source">
    /// The macro job that this message should be attached to.
    /// </param>
    /// <param name="queue">
    /// The job queue that micro jobs will be pushed into.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token for the macro job.
    /// For efficiency, all the micro jobs expanded from
    /// this macro job will share the same cancellation source,
    /// and micro jobs cannot be cancelled independently
    /// of one another.
    /// </param>
    internal MacroJobMessage(in PromisedWork work,
                             MacroJob source,
                             ClientJobQueue queue,
                             CancellationToken cancellationToken)
    {
        Source = source;
        _queue = queue;
        ClientToken = cancellationToken;

        _listLinks = new(this);
        _isInvalid = Source.AddParticipant(this) ? 0 : -1;
    }

    public bool Cancel(bool background)
    {
        CancellationTokenSource? source;

        lock (this)
        {
            // Nothing to do if already cancelled.
            if (_isCancelled)
                return false;

            _isCancelled = true;

            // Exchange the rented source out, to prevent concurrent
            // returning of it, which is not allowed anyway once
            // cancellation has been triggered.
            source = _rentedCancellationSource.Source;
            _rentedCancellationSource = default;
        }

        source?.CancelMaybeInBackground(background);

        // Clean up immediately if this instance has not started
        // yet, i.e. it has not yet been de-queued.
        Dispose();

        return true;
    }

    /// <inheritdoc cref="IAsyncEnumerable{T}.GetAsyncEnumerator" />
    public async IAsyncEnumerator<JobMessage>
        GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        int wasInvalid = Interlocked.CompareExchange(ref _isInvalid, 1, 0);

        // Quit early if already disposed or cancelled
        if (wasInvalid == -1)
            yield break;

        if (wasInvalid == 1)
        {
            throw new NotSupportedException(
                        "Enumerator for MacroJobMessage may not be retrieved " +
                        "more than once. ");
        }

        int count = 0;
        Exception? exception = null;

        var jobCancelToken = new CancellationToken(canceled: true);

        JobsManager jobScheduling = Source.JobsManager;
        IPromiseListBuilder resultBuilder = Source.ResultBuilder;

        // Whether an exception indicates cancellation from the token
        // passed into this enumerator.
        static bool IsLocalCancellation(Exception e, CancellationToken c)
            => e is OperationCanceledException ec && ec.CancellationToken == c;

        //
        // We cannot write the following loop as a straightforward
        // "await foreach" with "yield return" inside, because
        // we need to catch exceptions.  We must control the
        // enumerator manually.
        //

        IAsyncEnumerator<(PromiseRetriever, PromisedWork)>? enumerator = null;
        try
        {
            // Do not do anything if another producer has already completed.
            if (resultBuilder.IsComplete)
            {
                BasicCleanUp();
                yield break;
            }

            // Rent cancellation source unless this job has already been
            // cancelled.  When this block is not executed, jobCancelToken
            // is in the cancelled state as initialized above.
            if (!_isCancelled && !ClientToken.IsCancellationRequested)
            {
                var rentedCancellationSource = CancellationSourcePool.Rent();

                lock (this)
                {
                    if (!_isCancelled)
                    {
                        jobCancelToken = rentedCancellationSource.Token;
                        _rentedCancellationSource = rentedCancellationSource;
                    }
                }

                // Link in client's original cancellation token, if any
                _clientCancellationRegistration = ClientToken.Register(
                    static s => Unsafe.As<MacroJobMessage>(s!).Cancel(background: true),
                    this);
            }

            if (!jobCancelToken.IsCancellationRequested)
                enumerator = Source.Expansion.GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception e) when (!IsLocalCancellation(e, cancellationToken))
        {
            exception = e;
        }

        if (enumerator is not null)
        {
            while (true)
            {
                JobMessage? message;

                try
                {
                    if (jobCancelToken.IsCancellationRequested)
                        break;

                    // Stop generating job messages if another producer
                    // has completely done so, or there are no more micro jobs.
                    if (resultBuilder.IsComplete ||
                        !await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;

                    if (jobCancelToken.IsCancellationRequested)
                        break;

                    var (promiseRetriever, input) = enumerator.Current;

                    message = jobScheduling.RegisterJobMessage(
                                    _queue.SchedulingAccount,
                                    promiseRetriever,
                                    input,
                                    registerClient: false,
                                    jobCancelToken,
                                    out var promise);

                    // Add the new member to the result sequence.
                    resultBuilder.SetMember(count, promise);
                    count++;
                }
                catch (Exception e) when (!IsLocalCancellation(e, cancellationToken))
                {
                    exception = e;
                    break;
                }

                // Do not schedule work if promise is already complete
                if (message is not null)
                    yield return message;
            }

            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (!IsLocalCancellation(e, cancellationToken))
            {
                exception ??= e;
            }
        }

        if (jobCancelToken.IsCancellationRequested && exception is null)
        {
            FailIfOnlyProducer(count, exception: null);
        }
        else
        {
            // When this producers finishes successfully without
            // job cancellation, complete resultBuilder.
            resultBuilder.TryComplete(count, exception, jobCancelToken);
            _ = FinishAsync();
        }
    }

    /// <summary>
    /// Unconditional synchronous clean-up action after this message
    /// finishes executing.
    /// </summary>
    /// <remarks>
    /// This method relies on the caller guarding it from being
    /// called more than once by the atomic variable 
    /// <see cref="_isInvalid" />.
    /// </remarks>
    /// <returns>
    /// True if this participant is the last for the (shared) macro job.
    /// </returns>
    private bool BasicCleanUp()
    {
        // We do not need to block on unregistering, because
        // the Cancel method already locks to prevent the
        // cancellation source from going away concurrently.
        _clientCancellationRegistration.Unregister();
        _clientCancellationRegistration = default;

        if (_isTrackingClientRequest)
            Source.JobsManager.UnregisterClientRequest(Source.PromiseId, ClientToken);

        return Source.RemoveParticipant(this);
    }

    /// <summary>
    /// Dispose of this message as part of backing out
    /// operations in case of an exception.
    /// </summary>
    /// <param name="exception">
    /// The exception to complete <see cref="MacroJob.ResultBuilder" />
    /// with.  If null, this instance is assumed to have cancelled
    /// and the exception will be taken as
    /// <see cref="OperationCanceledException" />.
    /// </param>
    public void DisposeWithException(Exception? exception)
    {
        if (Interlocked.CompareExchange(ref _isInvalid, -1, 0) == 0)
            FailIfOnlyProducer(count: 0, exception: null);
    }

    /// <summary>
    /// Complete the result only if this instance is 
    /// the only producer remaining.
    /// </summary>
    /// <param name="count">
    /// Number of items emitted by this producer so far,
    /// needed to complete <see cref="IPromiseListBuilder" />.
    /// </param>
    /// <param name="exception">
    /// The exception to complete <see cref="MacroJob.ResultBuilder" />
    /// with.  If null, this instance is assumed to have cancelled
    /// and the exception will be taken as
    /// <see cref="OperationCanceledException" />.
    /// </param>
    private void FailIfOnlyProducer(int count, Exception? exception)
    {
        // Note that the reference count behind BasicCleanUp is not
        // decremented until this point.  So, it is legal that all
        // participants have been requested to cancel, but before
        // they all process their cancellations to this point,
        // another participant can add itself and "resurrect" the
        // macro job.
        if (!BasicCleanUp())
            return;

        // Avoid creating an exception object if it would be ignored anyway
        var resultBuilder = Source.ResultBuilder;
        if (resultBuilder.IsComplete)
            return;

        if (exception is null)
        {
            var jobCancelToken = ClientToken.IsCancellationRequested
                ? ClientToken
                : new CancellationToken(canceled: true);

            exception = new OperationCanceledException(jobCancelToken);
        }

        resultBuilder.TryComplete(count, exception);
    }

    /// <summary>
    /// Wait for all promises to complete before executing 
    /// clean-up action, when this message has not been cancelled.
    /// </summary>
    private async Task FinishAsync()
    {
        try
        {
            await Source.ResultBuilder.WaitForAllPromisesAsync()
                                      .ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            BasicCleanUp();

            CancellationSourcePool.Use rentedCancellationSource;

            lock (this)
            {
                rentedCancellationSource = _rentedCancellationSource;
                _rentedCancellationSource = default;
            }

            rentedCancellationSource.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Unregister this instance from its master <see cref="MacroJob" />
    /// if it has not started its micro jobs yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is used to discard speculatively created instances
    /// of this class.  It has no effect when the micro jobs have
    /// already started to be enumerated, or when this instance
    /// is already disposed.
    /// </para>
    /// <para>
    /// This method also sets an exception on the promise if this
    /// instance would have been the only producer, so that it
    /// is not stuck in an incompleted state.
    /// </para>
    /// </remarks>
    public void Dispose() => DisposeWithException(null);

    bool IJobCancellation.CancelForClient(CancellationToken clientToken, 
                                          bool background)
        => (clientToken == ClientToken) && Cancel(background);

    void IJobCancellation.Kill(bool background)
        => Source.Kill(background);
}

/// <summary>
/// Object which is shared by instances of <see cref="MacroJobMessage" />
/// that refer to the same macro job.
/// </summary>
internal sealed class MacroJob : IJobCancellation
{
    public readonly JobsManager JobsManager;
    public readonly IPromiseListBuilder ResultBuilder;
    public readonly PromiseId PromiseId;
    public readonly MacroJobExpansion Expansion;

    private MacroJobMessage? _participants;
    private int _count;

    /// <summary>
    /// Construct with information about the job 
    /// shared instances of <see cref="MacroJobMessage" />.
    /// </summary>
    /// <param name="jobsManager">
    /// The job scheduling system that this message is for.
    /// This reference is needed to push micro jobs into
    /// the job queue.
    /// </param>
    /// <param name="resultBuilder">
    /// The list of promises generated by <paramref name="expansion" />
    /// will be stored/passed onto here.
    /// </param>
    /// <param name="promiseId">
    /// The promise ID for the macro job, needed to unregister
    /// it from <paramref name="jobsManager" /> when the macro
    /// job has finished expanding.
    /// </param>
    /// <param name="expansion">
    /// User-supplied generator that lists out
    /// the promise objects and work descriptions for
    /// the micro jobs.
    /// </param>
    public MacroJob(JobsManager jobsManager,
                    IPromiseListBuilder resultBuilder,
                    PromiseId promiseId,
                    MacroJobExpansion expansion)
    {
        JobsManager = jobsManager;
        ResultBuilder = resultBuilder;
        PromiseId = promiseId;
        Expansion = expansion;
    }

    bool IJobCancellation.CancelForClient(CancellationToken clientToken, 
                                          bool background)
    {
        MacroJobMessage? q;

        lock (this)
        {
            var p = q = _participants;

            while (q is not null)
            {
                if (q.ClientToken == clientToken)
                    break;

                var r = q.ListLinks.Next;
                q = ReferenceEquals(r, p) ? null : r;
            }
        }

        return q?.Cancel(background) ?? false;
    }

    /// <inheritdoc cref="IJobCancellation.Kill" />
    public void Kill(bool background)
    {
        MacroJobMessage? q;

        lock (this)
        {
            var p = q = _participants;

            while (q is not null)
            {
                // Get next link first, in case q.Cancel asynchronously
                // removes q from the linked list.
                var r = q.ListLinks.Next;
                r = ReferenceEquals(r, p) ? null : r;

                q.Cancel(background);
                q = r;
            }
        }
    }

    /// <summary>
    /// Register a participant from this (shared) macro job.
    /// </summary>
    /// <remarks>
    /// If this method returns true, the participant
    /// will have been added to <see cref="_participants" />,
    /// and <see cref="_count" /> will be incremented by one.
    /// </remarks>
    /// <returns>
    /// True if the participant has been successfully added;
    /// false if all other participants have cancelled and 
    /// this instance is no longer valid to start executing
    /// the (shared) macro job.
    /// </returns>
    internal bool AddParticipant(MacroJobMessage message)
    {
        lock (this)
        {
            MacroJobLinks.Append(message, ref _participants);

            var count = _count;
            if (count < 0)
                return false;
            _count = ++count;
        }

        return true;
    }

    /// <summary>
    /// Unregister a participant from this (shared) macro job.
    /// </summary>
    /// <remarks>
    /// It is removed from <see cref="_participants" />,
    /// and <see cref="_count" /> is decremented by one.
    /// </remarks>
    /// <returns>
    /// True if the participant being removed 
    /// is the last for this (shared) macro job.
    /// </returns>
    internal bool RemoveParticipant(MacroJobMessage message)
    {
        bool dead;

        lock (this)
        {
            MacroJobLinks.Remove(message, ref _participants);
            int remaining = _count - 1;
            dead = (remaining <= 0);
            _count = dead ? -1 : remaining;
        }

        if (dead)
            JobsManager.UnregisterMacroJob(PromiseId);

        return dead;
    }
}
