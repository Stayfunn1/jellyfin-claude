using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.GroupStates;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using SyncPlayGroup = Emby.Server.Implementations.SyncPlay.Group;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay;

/// <summary>
/// Drives a group through its public request handling, the way the API does, and checks what the
/// members are told.
/// </summary>
public class GroupPlaybackFlowTests
{
    [Fact]
    public void Ready_GroupPausedWhileWaitingForABufferingMember_StaysWhereTheWaitStarted()
    {
        var harness = new FlowHarness();
        harness.StartPlaying();

        // The group has been playing for a minute from the ten minute mark.
        harness.Group.PositionTicks = TimeSpan.FromMinutes(10).Ticks;
        harness.Group.LastActivity = DateTime.UtcNow.AddMinutes(-1);

        // The second member stalls, so the group pauses everyone where it currently is.
        harness.Handle(harness.Second, new BufferGroupRequest(DateTime.UtcNow, TimeSpan.FromMinutes(11).Ticks, true, harness.PlayingItem));
        Assert.Equal(GroupStateType.Waiting, harness.State);
        var frozenPosition = harness.Group.PositionTicks;

        // Someone presses pause while the group is waiting.
        harness.Handle(harness.First, new PauseGroupRequest());

        // The stalled member takes five minutes to recover.
        harness.Group.LastActivity = DateTime.UtcNow.AddMinutes(-5);
        harness.Commands.Clear();
        harness.Handle(harness.Second, new ReadyGroupRequest(DateTime.UtcNow, frozenPosition, false, harness.PlayingItem));

        // The group was standing still the whole time, so nobody may be sent five minutes ahead.
        Assert.Equal(GroupStateType.Paused, harness.State);
        Assert.Equal(frozenPosition, harness.Group.PositionTicks);
        var pause = Assert.Single(harness.Commands.Where(c => c.Command.Command == SendCommandType.Pause).Select(c => c.Command).Distinct());
        Assert.Equal(frozenPosition, pause.PositionTicks);
    }

    [Fact]
    public void Ready_GroupStartedFromIdleAndPausedWhileLoading_TellsEveryoneItIsPaused()
    {
        var harness = new FlowHarness();

        harness.Handle(harness.First, new PlayGroupRequest(new[] { harness.ItemId }, 0, 0));
        var playingItem = harness.Group.PlayQueue.GetPlayingItemPlaylistId();
        harness.Handle(harness.First, new PauseGroupRequest());

        harness.StateUpdates.Clear();
        harness.Commands.Clear();
        harness.Handle(harness.First, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playingItem));
        harness.Handle(harness.Second, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playingItem));

        Assert.Equal(GroupStateType.Paused, harness.State);

        // Without this the members keep showing a group that waits for someone.
        foreach (var session in new[] { harness.First, harness.Second })
        {
            Assert.Contains(harness.StateUpdates, u => u.SessionId == session.Id && u.Update.State == GroupStateType.Paused);
            Assert.Contains(harness.Commands, c => c.SessionId == session.Id && c.Command.Command == SendCommandType.Pause);
        }
    }

    [Fact]
    public void Buffer_ForAnItemTheGroupAlreadyLeft_DoesNotStopThePlayingGroup()
    {
        var harness = new FlowHarness();
        harness.StartPlaying();
        harness.Commands.Clear();

        // A buffering report that raced a change of item arrives after the group moved on.
        harness.Handle(harness.Second, new BufferGroupRequest(DateTime.UtcNow, 0, true, Guid.NewGuid()));

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.DoesNotContain(harness.Commands, c => c.Command.Command == SendCommandType.Pause);

        // The stale member is told what the group is playing.
        Assert.Contains(harness.PlayQueueUpdates, u => u.SessionId == harness.Second.Id);
    }

    [Fact]
    public void NextItem_DuplicateRequestArrivingWhilePlaying_KeepsTheGroupPlaying()
    {
        var harness = new FlowHarness(itemCount: 2);
        harness.StartPlaying();
        var firstItem = harness.PlayingItem;

        // Both members finish the first item and ask for the next one.
        harness.Handle(harness.First, new NextItemGroupRequest(firstItem));
        var secondItem = harness.Group.PlayQueue.GetPlayingItemPlaylistId();
        harness.Handle(harness.First, new ReadyGroupRequest(DateTime.UtcNow, 0, false, secondItem));
        harness.Handle(harness.Second, new ReadyGroupRequest(DateTime.UtcNow, 0, false, secondItem));
        Assert.Equal(GroupStateType.Playing, harness.State);

        // The slower member's request only arrives once the group already plays the next item.
        harness.Handle(harness.Second, new NextItemGroupRequest(firstItem));

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.Equal(secondItem, harness.Group.PlayQueue.GetPlayingItemPlaylistId());
    }

    [Fact]
    public void Play_RequestThatCannotBePlayedWhileWaiting_KeepsWaitingForTheSameMembers()
    {
        var harness = new FlowHarness();
        harness.Handle(harness.First, new PlayGroupRequest(new[] { harness.ItemId }, 0, 0));
        Assert.Equal(GroupStateType.Waiting, harness.State);

        // An out of range request changes nothing, so it must not silently drop the group to idle.
        harness.Handle(harness.Second, new PlayGroupRequest(new[] { harness.ItemId }, 5, 0));

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.True(harness.Group.IsBuffering());
    }

    [Fact]
    public void Ready_SessionThatCannotReachTheGroupPosition_IsAcceptedAfterAFewCorrections()
    {
        var harness = new FlowHarness();
        harness.StartPlaying();

        var target = TimeSpan.FromMinutes(20).Ticks;
        harness.Handle(harness.First, new SeekGroupRequest(target));
        harness.Handle(harness.First, new ReadyGroupRequest(DateTime.UtcNow, target, false, harness.PlayingItem));

        // The other player keeps landing three seconds early, e.g. on a segment boundary.
        var landed = target - TimeSpan.FromSeconds(3).Ticks;
        for (var i = 0; i < WaitingGroupState.MaxPositionCorrections; i++)
        {
            harness.Commands.Clear();
            harness.Handle(harness.Second, new ReadyGroupRequest(DateTime.UtcNow, landed, false, harness.PlayingItem));
            Assert.Contains(harness.Commands, c => c.SessionId == harness.Second.Id && c.Command.Command == SendCommandType.Seek);
            Assert.Equal(GroupStateType.Waiting, harness.State);
        }

        // Seeking it again would only repeat the same error, so the group starts.
        harness.Commands.Clear();
        harness.Handle(harness.Second, new ReadyGroupRequest(DateTime.UtcNow, landed, false, harness.PlayingItem));

        Assert.Equal(GroupStateType.Playing, harness.State);
        Assert.DoesNotContain(harness.Commands, c => c.Command.Command == SendCommandType.Seek);
        Assert.All(
            harness.Commands.Where(c => c.Command.Command == SendCommandType.Unpause),
            c => Assert.Equal(target, c.Command.PositionTicks));
    }

    [Fact]
    public void Seek_ItemWithUnknownRuntime_KeepsTheRequestedPosition()
    {
        var harness = new FlowHarness(runTimeTicks: null);
        harness.StartPlaying();

        var target = TimeSpan.FromMinutes(42).Ticks;
        harness.Handle(harness.First, new SeekGroupRequest(target));

        Assert.Equal(target, harness.Group.PositionTicks);
        Assert.Contains(harness.Commands, c => c.Command.Command == SendCommandType.Seek && c.Command.PositionTicks == target);
    }

    [Fact]
    public void NextItem_NextItemRemovedFromTheLibrary_DoesNotBreakTheGroup()
    {
        var harness = new FlowHarness(itemCount: 2);
        harness.StartPlaying();

        harness.LibraryItems.Remove(harness.ItemIds[1]);
        harness.Handle(harness.First, new NextItemGroupRequest(harness.PlayingItem));

        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.Equal(harness.ItemIds[1], harness.Group.PlayQueue.GetPlayingItemId());
    }

    [Fact]
    public void CreateGroup_WhilePlayingWithoutAReportedQueue_TheGroupPlaysTheCurrentItem()
    {
        var harness = new FlowHarness(joinSecond: false, startPlaying: false);
        var session = harness.NewSession("watcher");
        harness.SetNowPlaying(session, harness.ItemId, TimeSpan.FromMinutes(7).Ticks, isPaused: false);

        // Many clients never report their queue.
        session.NowPlayingQueue = [];

        var group = harness.NewGroup();
        group.CreateGroup(session, new NewGroupRequest("group"), CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);
        Assert.Equal(harness.ItemId, group.PlayQueue.GetPlayingItemId());
        Assert.Equal(TimeSpan.FromMinutes(7).Ticks, group.PositionTicks);
    }

    [Fact]
    public void SessionJoin_IdleGroupJoinedByAMemberThatIsWatching_TheGroupTakesOverItsPlayback()
    {
        var harness = new FlowHarness(joinSecond: false, startPlaying: false);
        harness.SetNowPlaying(harness.Second, harness.ItemId, TimeSpan.FromMinutes(3).Ticks, isPaused: false);
        harness.PlayQueueUpdates.Clear();
        harness.Commands.Clear();

        harness.Group.SessionJoin(harness.Second, new JoinGroupRequest(harness.Group.GroupId), CancellationToken.None);

        // The joiner keeps watching, and the member that was already there is brought along.
        Assert.Equal(GroupStateType.Waiting, harness.State);
        Assert.Equal(harness.ItemId, harness.Group.PlayQueue.GetPlayingItemId());
        Assert.Equal(TimeSpan.FromMinutes(3).Ticks, harness.Group.PositionTicks);
        Assert.Contains(harness.PlayQueueUpdates, u => u.SessionId == harness.First.Id);
        Assert.Contains(harness.PlayQueueUpdates, u => u.SessionId == harness.Second.Id);
        Assert.DoesNotContain(harness.Commands, c => c.Command.Command == SendCommandType.Stop);
        Assert.True(harness.Group.IsBuffering());
    }

    [Fact]
    public void SessionJoin_GroupThatAlreadyPlaysSomething_TheJoinerFollowsTheGroup()
    {
        var harness = new FlowHarness(joinSecond: false, startPlaying: false);
        harness.Handle(harness.First, new PlayGroupRequest(new[] { harness.ItemId }, 0, 0));
        var groupItem = harness.Group.PlayQueue.GetPlayingItemPlaylistId();

        var other = harness.AddLibraryItem();
        harness.SetNowPlaying(harness.Second, other, 0, isPaused: false);
        harness.Group.SessionJoin(harness.Second, new JoinGroupRequest(harness.Group.GroupId), CancellationToken.None);

        Assert.Equal(groupItem, harness.Group.PlayQueue.GetPlayingItemPlaylistId());
        Assert.Equal(harness.ItemId, harness.Group.PlayQueue.GetPlayingItemId());
    }

    private sealed class FlowHarness
    {
        private readonly Mock<ISessionManager> _sessionManager = new();
        private readonly Mock<IUserManager> _userManager = new();
        private readonly Mock<ILibraryManager> _libraryManager = new();
        private readonly User _user = new("tester", "auth-provider", "pwdreset-provider");
        private readonly long? _runTimeTicks;

        public FlowHarness(int itemCount = 1, long? runTimeTicks = 72_000_000_000, bool joinSecond = true, bool startPlaying = false)
        {
            _runTimeTicks = runTimeTicks;
            _userManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns(_user);
            _libraryManager
                .Setup(m => m.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid id) => LibraryItems.TryGetValue(id, out var item) ? item : null);

            for (var i = 0; i < itemCount; i++)
            {
                ItemIds.Add(AddLibraryItem());
            }

            _sessionManager
                .Setup(m => m.SendSyncPlayCommand(It.IsAny<string>(), It.IsAny<SendCommand>(), It.IsAny<CancellationToken>()))
                .Callback((string sessionId, SendCommand command, CancellationToken _) => Commands.Add((sessionId, command)))
                .Returns(Task.CompletedTask);
            _sessionManager
                .Setup(m => m.SendSyncPlayGroupUpdate(It.IsAny<string>(), It.IsAny<GroupUpdate<GroupStateUpdate>>(), It.IsAny<CancellationToken>()))
                .Callback((string sessionId, GroupUpdate<GroupStateUpdate> update, CancellationToken _) => StateUpdates.Add((sessionId, update.Data)))
                .Returns(Task.CompletedTask);
            _sessionManager
                .Setup(m => m.SendSyncPlayGroupUpdate(It.IsAny<string>(), It.IsAny<GroupUpdate<PlayQueueUpdate>>(), It.IsAny<CancellationToken>()))
                .Callback((string sessionId, GroupUpdate<PlayQueueUpdate> update, CancellationToken _) => PlayQueueUpdates.Add((sessionId, update.Data)))
                .Returns(Task.CompletedTask);

            First = NewSession("first");
            Second = NewSession("second");

            Group = NewGroup();
            Group.CreateGroup(First, new NewGroupRequest("group"), CancellationToken.None);
            if (joinSecond)
            {
                Group.SessionJoin(Second, new JoinGroupRequest(Group.GroupId), CancellationToken.None);
            }

            if (startPlaying)
            {
                StartPlaying();
            }
        }

        public SyncPlayGroup Group { get; }

        public SessionInfo First { get; }

        public SessionInfo Second { get; }

        public List<Guid> ItemIds { get; } = new();

        public Guid ItemId => ItemIds[0];

        public Dictionary<Guid, BaseItem> LibraryItems { get; } = new();

        public Guid PlayingItem => Group.PlayQueue.GetPlayingItemPlaylistId();

        public GroupStateType State => Group.GetInfo().State;

        public List<(string SessionId, SendCommand Command)> Commands { get; } = new();

        public List<(string SessionId, GroupStateUpdate Update)> StateUpdates { get; } = new();

        public List<(string SessionId, PlayQueueUpdate Update)> PlayQueueUpdates { get; } = new();

        public Guid AddLibraryItem()
        {
            var item = new Mock<BaseItem>();
            item.Setup(i => i.IsVisibleStandalone(It.IsAny<User>())).Returns(true);
            item.Object.Id = Guid.NewGuid();
            item.Object.RunTimeTicks = _runTimeTicks;
            LibraryItems[item.Object.Id] = item.Object;
            return item.Object.Id;
        }

        public SyncPlayGroup NewGroup()
        {
            return new SyncPlayGroup(NullLoggerFactory.Instance, _userManager.Object, _sessionManager.Object, _libraryManager.Object);
        }

        public SessionInfo NewSession(string id)
        {
            return new SessionInfo(_sessionManager.Object, NullLogger.Instance)
            {
                Id = id,
                UserId = _user.Id,
                UserName = id
            };
        }

        public void SetNowPlaying(SessionInfo session, Guid itemId, long positionTicks, bool isPaused)
        {
            session.FullNowPlayingItem = LibraryItems[itemId];
            session.NowPlayingQueue = [new QueueItem { Id = itemId }];
            session.PlayState = new PlayerStateInfo { PositionTicks = positionTicks, IsPaused = isPaused };
        }

        public void Handle(SessionInfo session, IGroupPlaybackRequest request)
        {
            Group.HandleRequest(session, request, CancellationToken.None);
        }

        /// <summary>
        /// Puts the whole queue in the group and has both members report ready, so the group plays.
        /// </summary>
        public void StartPlaying()
        {
            Handle(First, new PlayGroupRequest(ItemIds, 0, 0));
            var playingItem = PlayingItem;
            Handle(First, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playingItem));
            Handle(Second, new ReadyGroupRequest(DateTime.UtcNow, 0, false, playingItem));
            Assert.Equal(GroupStateType.Playing, State);
            Commands.Clear();
            StateUpdates.Clear();
            PlayQueueUpdates.Clear();
        }
    }
}
