#nullable disable

using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.SyncPlay.GroupStates
{
    /// <summary>
    /// Class WaitingGroupState.
    /// </summary>
    /// <remarks>
    /// Class is not thread-safe, external locking is required when accessing methods.
    /// </remarks>
    public class WaitingGroupState : AbstractGroupState
    {
        /// <summary>
        /// How many times a session is seeked back into position during a single wait before the
        /// group accepts the position it reports.
        /// </summary>
        /// <remarks>
        /// Players do not always land exactly where they are seeked to, e.g. a transcoded stream
        /// restarts on a segment boundary. Correcting such a session again only reproduces the same
        /// error, so after a few attempts the group starts anyway and leaves the remaining drift to
        /// the clients' own sync correction.
        /// </remarks>
        internal const int MaxPositionCorrections = 3;

        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger<WaitingGroupState> _logger;

        /// <summary>
        /// The number of position corrections sent to each session during the current wait.
        /// </summary>
        private readonly Dictionary<string, int> _positionCorrections = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="WaitingGroupState"/> class.
        /// </summary>
        /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
        public WaitingGroupState(ILoggerFactory loggerFactory)
            : base(loggerFactory)
        {
            _logger = LoggerFactory.CreateLogger<WaitingGroupState>();
        }

        /// <inheritdoc />
        public override GroupStateType Type { get; } = GroupStateType.Waiting;

        /// <summary>
        /// Gets or sets a value indicating whether playback should resume when group is ready.
        /// </summary>
        public bool ResumePlaying { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether the group position moved during this wait.
        /// </summary>
        private bool PositionJumped { get; set; }

        /// <inheritdoc />
        public override void SessionJoined(IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            if (prevState.Equals(GroupStateType.Playing))
            {
                ResumePlaying = true;
                // Pause group and compute the media playback position.
                var currentTime = DateTime.UtcNow;
                var elapsedTime = currentTime - context.LastActivity;
                context.LastActivity = currentTime;
                // Elapsed time is negative if event happens
                // during the delay added to account for latency.
                // In this phase clients haven't started the playback yet.
                // In other words, LastActivity is in the future,
                // when playback unpause is supposed to happen.
                // Seek only if playback actually started.
                context.PositionTicks += Math.Max(elapsedTime.Ticks, 0);
            }

            // Prepare new session.
            var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.NewPlaylist);
            var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
            context.SendGroupUpdate(session, SyncPlayBroadcastType.CurrentSession, update, cancellationToken);

            context.SetBuffering(session, true);

            // Send pause command to all non-buffering sessions.
            var command = context.NewSyncPlayCommand(SendCommandType.Pause);
            context.SendCommand(session, SyncPlayBroadcastType.AllReady, command, cancellationToken);
        }

        /// <inheritdoc />
        public override void SessionLeaving(IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            context.SetBuffering(session, false);

            if (!context.IsBuffering())
            {
                if (ResumePlaying)
                {
                    _logger.LogDebug("Session {SessionId} left group {GroupId}, notifying others to resume.", session.Id, context.GroupId.ToString());

                    // Client, that was buffering, left the group.
                    var playingState = new PlayingGroupState(LoggerFactory);
                    context.SetState(playingState);
                    var unpauseRequest = new UnpauseGroupRequest();
                    playingState.HandleRequest(unpauseRequest, context, Type, session, cancellationToken);
                }
                else
                {
                    _logger.LogDebug("Session {SessionId} left group {GroupId}, returning to previous state.", session.Id, context.GroupId.ToString());

                    // Group is ready, returning to previous state.
                    var pausedState = new PausedGroupState(LoggerFactory);
                    context.SetState(pausedState);
                }
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(PlayGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            var setQueueStatus = context.SetPlayQueue(request.PlayingQueue, request.PlayingItemPosition, request.StartPositionTicks);
            if (!setQueueStatus)
            {
                _logger.LogError("Unable to set playing queue in group {GroupId}.", context.GroupId.ToString());

                // Ignore request and return to previous state.
                ReturnToPreviousState(context, prevState);
                return;
            }

            ResumePlaying = true;
            MarkPositionJumped();

            var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.NewPlaylist);
            var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
            context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

            // Reset status of sessions and await for all Ready events.
            context.SetAllBuffering(true);

            _logger.LogDebug("Session {SessionId} set a new play queue in group {GroupId}.", session.Id, context.GroupId.ToString());
        }

        /// <inheritdoc />
        public override void HandleRequest(SetPlaylistItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            var result = context.SetPlayingItem(request.PlaylistItemId);
            if (result)
            {
                ResumePlaying = true;
                MarkPositionJumped();

                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.SetCurrentItem);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

                // Reset status of sessions and await for all Ready events.
                context.SetAllBuffering(true);
            }
            else
            {
                // Return to old state.
                ReturnToPreviousState(context, prevState);

                _logger.LogDebug("Unable to change current playing item in group {GroupId}.", context.GroupId.ToString());
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(UnpauseGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            if (prevState.Equals(GroupStateType.Idle))
            {
                ResumePlaying = true;
                context.RestartCurrentItem();
                MarkPositionJumped();

                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.NewPlaylist);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

                // Reset status of sessions and await for all Ready events.
                context.SetAllBuffering(true);

                _logger.LogDebug("Group {GroupId} is waiting for all ready events.", context.GroupId.ToString());
            }
            else
            {
                if (ResumePlaying)
                {
                    _logger.LogDebug("Forcing the playback to start in group {GroupId}. Group-wait is disabled until next state change.", context.GroupId.ToString());

                    // An Unpause request is forcing the playback to start, ignoring sessions that are not ready.
                    context.SetAllBuffering(false);

                    // Change state.
                    var playingState = new PlayingGroupState(LoggerFactory)
                    {
                        IgnoreBuffering = true
                    };
                    context.SetState(playingState);
                    playingState.HandleRequest(request, context, Type, session, cancellationToken);
                }
                else
                {
                    // Group would have gone to paused state, now will go to playing state when ready.
                    ResumePlaying = true;

                    // Notify relevant state change event.
                    SendGroupStateUpdate(context, request, session, cancellationToken);
                }
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(PauseGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Wait for sessions to be ready, then switch to paused state.
            ResumePlaying = false;

            // Notify relevant state change event.
            SendGroupStateUpdate(context, request, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(StopGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var idleState = new IdleGroupState(LoggerFactory);
            context.SetState(idleState);
            idleState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(SeekGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            if (prevState.Equals(GroupStateType.Playing))
            {
                ResumePlaying = true;
            }
            else if (prevState.Equals(GroupStateType.Paused))
            {
                ResumePlaying = false;
            }

            // Sanitize PositionTicks.
            var ticks = context.SanitizePositionTicks(request.PositionTicks);

            // Seek.
            context.PositionTicks = ticks;
            context.LastActivity = DateTime.UtcNow;
            MarkPositionJumped();

            var command = context.NewSyncPlayCommand(SendCommandType.Seek);
            context.SendCommand(session, SyncPlayBroadcastType.AllGroup, command, cancellationToken);

            // Reset status of sessions and await for all Ready events.
            context.SetAllBuffering(true);

            // Notify relevant state change event.
            SendGroupStateUpdate(context, request, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(BufferGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Make sure the client is playing the correct item.
            if (!request.PlaylistItemId.Equals(context.PlayQueue.GetPlayingItemPlaylistId()))
            {
                _logger.LogDebug("Session {SessionId} reported wrong playlist item in group {GroupId}.", session.Id, context.GroupId.ToString());

                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.SetCurrentItem);
                var updateSession = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.CurrentSession, updateSession, cancellationToken);

                if (prevState is GroupStateType.Playing or GroupStateType.Paused)
                {
                    // The report is about an item the group already moved past, e.g. it raced a
                    // next-item request. The rest of the group is not affected by it: holding them
                    // back here would leave them playing while the group believes it is paused,
                    // with a position that stopped advancing. The session catches up from the
                    // update above like a member that was just told to change item.
                    ReturnToPreviousState(context, prevState);
                    return;
                }

                context.SetBuffering(session, true);

                return;
            }

            if (prevState.Equals(GroupStateType.Playing))
            {
                // Resume playback when all ready.
                ResumePlaying = true;

                context.SetBuffering(session, true);

                // Pause group and compute the media playback position.
                var currentTime = DateTime.UtcNow;
                var elapsedTime = currentTime - context.LastActivity;
                context.LastActivity = currentTime;
                // Elapsed time is negative if event happens
                // during the delay added to account for latency.
                // In this phase clients haven't started the playback yet.
                // In other words, LastActivity is in the future,
                // when playback unpause is supposed to happen.
                // Seek only if playback actually started.
                context.PositionTicks += Math.Max(elapsedTime.Ticks, 0);

                // Send pause command to all non-buffering sessions.
                var command = context.NewSyncPlayCommand(SendCommandType.Pause);
                context.SendCommand(session, SyncPlayBroadcastType.AllReady, command, cancellationToken);
            }
            else if (prevState.Equals(GroupStateType.Paused))
            {
                // Don't resume playback when all ready.
                ResumePlaying = false;

                context.SetBuffering(session, true);

                // Send pause command to buffering session.
                var command = context.NewSyncPlayCommand(SendCommandType.Pause);
                context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);
            }
            else if (prevState.Equals(GroupStateType.Waiting))
            {
                // Another session is now buffering.
                context.SetBuffering(session, true);

                if (!ResumePlaying)
                {
                    // Force update for this session that should be paused.
                    var command = context.NewSyncPlayCommand(SendCommandType.Pause);
                    context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);
                }
            }

            // Notify relevant state change event.
            SendGroupStateUpdate(context, request, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(ReadyGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Make sure the client is playing the correct item.
            if (!request.PlaylistItemId.Equals(context.PlayQueue.GetPlayingItemPlaylistId()))
            {
                _logger.LogDebug("Session {SessionId} reported wrong playlist item in group {GroupId}.", session.Id, context.GroupId.ToString());

                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.SetCurrentItem);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.CurrentSession, update, cancellationToken);
                context.SetBuffering(session, true);

                return;
            }

            // Compute elapsed time between the client reported time and now.
            // Elapsed time is used to estimate the client position when playback is unpaused.
            // Ideally, the request is received and handled without major delays.
            // However, to avoid waiting indefinitely when a client is not reporting a correct time,
            // the elapsed time is ignored after a certain threshold.
            var currentTime = DateTime.UtcNow;
            var elapsedTime = currentTime.Subtract(request.When);
            var timeSyncThresholdTicks = TimeSpan.FromMilliseconds(context.TimeSyncOffset).Ticks;
            if (Math.Abs(elapsedTime.Ticks) > timeSyncThresholdTicks)
            {
                _logger.LogWarning("Session {SessionId} is not time syncing properly. Ignoring elapsed time.", session.Id);

                elapsedTime = TimeSpan.Zero;
            }

            // Ignore elapsed time if client is paused.
            if (!request.IsPlaying)
            {
                elapsedTime = TimeSpan.Zero;
            }

            var requestTicks = context.SanitizePositionTicks(request.PositionTicks);
            var clientPosition = TimeSpan.FromTicks(requestTicks) + elapsedTime;
            var delayTicks = context.PositionTicks - clientPosition.Ticks;
            var maxPlaybackOffsetTicks = TimeSpan.FromMilliseconds(context.MaxPlaybackOffset).Ticks;

            _logger.LogDebug("Session {SessionId} is at {PositionTicks} (delay of {Delay} seconds) in group {GroupId}.", session.Id, clientPosition, TimeSpan.FromTicks(delayTicks).TotalSeconds, context.GroupId.ToString());

            if (ResumePlaying)
            {
                // Handle case where session reported as ready but in reality
                // it has no clue of the real position nor the playback state.
                // A jump means the session has not applied the new position; without one it is
                // catching up after buffering and is allowed to lag.
                var maxOffsetTicks = request.IsPlaying && !PositionJumped
                    ? TimeSpan.FromMilliseconds(context.MaxCatchUpOffset).Ticks
                    : maxPlaybackOffsetTicks;

                if (Math.Abs(delayTicks) > maxOffsetTicks)
                {
                    if (TryCorrectPosition(context, request, session, cancellationToken))
                    {
                        _logger.LogWarning("Session {SessionId} got lost in time, correcting.", session.Id);
                        return;
                    }

                    // The session cannot get any closer, so the group starts from its own position
                    // and the session's player syncs up once playback is running.
                    delayTicks = 0;
                }

                // Session is ready.
                context.SetBuffering(session, false);

                if (context.IsBuffering())
                {
                    // Others are still buffering, tell this client to pause when ready.
                    var command = context.NewSyncPlayCommand(SendCommandType.Pause);
                    command.When = currentTime.AddTicks(delayTicks);
                    context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);

                    _logger.LogInformation("Session {SessionId} will pause when ready in {Delay} seconds. Group {GroupId} is waiting for all ready events.", session.Id, TimeSpan.FromTicks(delayTicks).TotalSeconds, context.GroupId.ToString());
                }
                else
                {
                    // If all ready, then start playback.
                    // Let other clients resume as soon as the buffering client catches up.
                    if (delayTicks > context.GetHighestPing() * 2 * TimeSpan.TicksPerMillisecond)
                    {
                        // Client that was buffering is recovering, notifying others to resume.
                        context.LastActivity = currentTime.AddTicks(delayTicks);
                        var command = context.NewSyncPlayCommand(SendCommandType.Unpause);
                        var filter = SyncPlayBroadcastType.AllExceptCurrentSession;
                        if (!request.IsPlaying)
                        {
                            filter = SyncPlayBroadcastType.AllGroup;
                        }

                        context.SendCommand(session, filter, command, cancellationToken);

                        _logger.LogInformation("Session {SessionId} is recovering, group {GroupId} will resume in {Delay} seconds.", session.Id, context.GroupId.ToString(), TimeSpan.FromTicks(delayTicks).TotalSeconds);
                    }
                    else
                    {
                        // Client, that was buffering, resumed playback but did not update others in time.
                        delayTicks = context.GetHighestPing() * 2 * TimeSpan.TicksPerMillisecond;
                        delayTicks = Math.Max(delayTicks, TimeSpan.FromMilliseconds(context.DefaultPing).Ticks);

                        context.LastActivity = currentTime.AddTicks(delayTicks);

                        var command = context.NewSyncPlayCommand(SendCommandType.Unpause);
                        context.SendCommand(session, SyncPlayBroadcastType.AllGroup, command, cancellationToken);

                        _logger.LogWarning("Session {SessionId} resumed playback, group {GroupId} has {Delay} seconds to recover.", session.Id, context.GroupId.ToString(), TimeSpan.FromTicks(delayTicks).TotalSeconds);
                    }

                    // Change state.
                    var playingState = new PlayingGroupState(LoggerFactory);
                    context.SetState(playingState);
                    playingState.HandleRequest(request, context, Type, session, cancellationToken);
                }
            }
            else
            {
                // Check that session is really ready, tolerate player imperfections under a certain threshold.
                if (Math.Abs(context.PositionTicks - requestTicks) > maxPlaybackOffsetTicks
                    && TryCorrectPosition(context, request, session, cancellationToken))
                {
                    _logger.LogWarning("Session {SessionId} is seeking to wrong position, correcting.", session.Id);
                    return;
                }

                // Session is ready.
                context.SetBuffering(session, false);

                if (!context.IsBuffering())
                {
                    _logger.LogDebug("Session {SessionId} is ready, group {GroupId} is ready.", session.Id, context.GroupId.ToString());

                    // Group is ready, returning to previous state.
                    // The group has been paused for the whole wait, whichever state it came from: the
                    // position was frozen when the wait started, or set by the request that started it,
                    // and every member was checked against it above. Everyone is told where the group
                    // stands, including the members that never had to wait.
                    var pausedState = new PausedGroupState(LoggerFactory);
                    context.SetState(pausedState);
                    pausedState.HandleRequest(request, context, Type, session, cancellationToken);
                }
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(NextItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Make sure the client knows the playing item, to avoid duplicate requests.
            if (!request.PlaylistItemId.Equals(context.PlayQueue.GetPlayingItemPlaylistId()))
            {
                _logger.LogDebug("Session {SessionId} provided the wrong playlist item for group {GroupId}.", session.Id, context.GroupId.ToString());

                // The request is a duplicate, so the group carries on with what it was doing.
                ReturnToPreviousState(context, prevState);
                return;
            }

            var newItem = context.NextItemInQueue();
            if (newItem)
            {
                ResumePlaying = true;
                MarkPositionJumped();

                // Send playing-queue update.
                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.NextItem);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

                // Reset status of sessions and await for all Ready events.
                context.SetAllBuffering(true);
            }
            else
            {
                // Return to old state.
                ReturnToPreviousState(context, prevState);

                _logger.LogDebug("No next item available in group {GroupId}.", context.GroupId.ToString());
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(PreviousItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Make sure the client knows the playing item, to avoid duplicate requests.
            if (!request.PlaylistItemId.Equals(context.PlayQueue.GetPlayingItemPlaylistId()))
            {
                _logger.LogDebug("Session {SessionId} provided the wrong playlist item for group {GroupId}.", session.Id, context.GroupId.ToString());

                // The request is a duplicate, so the group carries on with what it was doing.
                ReturnToPreviousState(context, prevState);
                return;
            }

            var newItem = context.PreviousItemInQueue();
            if (newItem)
            {
                ResumePlaying = true;
                MarkPositionJumped();

                // Send playing-queue update.
                var playQueueUpdate = context.GetPlayQueueUpdate(PlayQueueUpdateReason.PreviousItem);
                var update = new SyncPlayPlayQueueUpdate(context.GroupId, playQueueUpdate);
                context.SendGroupUpdate(session, SyncPlayBroadcastType.AllGroup, update, cancellationToken);

                // Reset status of sessions and await for all Ready events.
                context.SetAllBuffering(true);
            }
            else
            {
                // Return to old state.
                ReturnToPreviousState(context, prevState);

                _logger.LogDebug("No previous item available in group {GroupId}.", context.GroupId.ToString());
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(IgnoreWaitGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            context.SetIgnoreGroupWait(session, request.IgnoreWait);

            if (!context.IsBuffering())
            {
                _logger.LogDebug("Ignoring session {SessionId}, group {GroupId} is ready.", session.Id, context.GroupId.ToString());

                if (ResumePlaying)
                {
                    // Client, that was buffering, stopped following playback.
                    var playingState = new PlayingGroupState(LoggerFactory);
                    context.SetState(playingState);
                    var unpauseRequest = new UnpauseGroupRequest();
                    playingState.HandleRequest(unpauseRequest, context, Type, session, cancellationToken);
                }
                else
                {
                    // Group is ready, returning to previous state.
                    var pausedState = new PausedGroupState(LoggerFactory);
                    context.SetState(pausedState);
                }
            }
        }

        /// <summary>
        /// Puts the group back in the state it was in before a request that turned out to change nothing.
        /// </summary>
        /// <param name="context">The context of the state.</param>
        /// <param name="prevState">The state the group was in before the request.</param>
        private void ReturnToPreviousState(IGroupStateContext context, GroupStateType prevState)
        {
            IGroupState newState = prevState switch
            {
                GroupStateType.Playing => new PlayingGroupState(LoggerFactory),
                GroupStateType.Paused => new PausedGroupState(LoggerFactory),
                GroupStateType.Idle => new IdleGroupState(LoggerFactory),
                // The group was already waiting, and still is: the members it waits for are unchanged.
                _ => null
            };

            if (newState is not null)
            {
                context.SetState(newState);
            }
        }

        /// <summary>
        /// Records that the group position moved, which starts a new round of position checks.
        /// </summary>
        private void MarkPositionJumped()
        {
            PositionJumped = true;
            _positionCorrections.Clear();
        }

        /// <summary>
        /// Seeks a session that reported ready at the wrong position back to the group position,
        /// unless it has already been corrected <see cref="MaxPositionCorrections"/> times during this wait.
        /// </summary>
        /// <param name="context">The context of the state.</param>
        /// <param name="request">The ready request with the wrong position.</param>
        /// <param name="session">The session.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> if a correction was sent; <c>false</c> if the session's position has to be accepted.</returns>
        private bool TryCorrectPosition(IGroupStateContext context, ReadyGroupRequest request, SessionInfo session, CancellationToken cancellationToken)
        {
            _positionCorrections.TryGetValue(session.Id, out var corrections);
            if (corrections >= MaxPositionCorrections)
            {
                _logger.LogWarning(
                    "Session {SessionId} is still off position in group {GroupId} after {Corrections} corrections, accepting it.",
                    session.Id,
                    context.GroupId.ToString(),
                    corrections);
                return false;
            }

            _positionCorrections[session.Id] = corrections + 1;

            // Session not ready at all.
            context.SetBuffering(session, true);

            // Correcting session's position.
            var command = context.NewSyncPlayCommand(SendCommandType.Seek);
            context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);

            // Notify relevant state change event.
            SendGroupStateUpdate(context, request, session, cancellationToken);
            return true;
        }
    }
}
