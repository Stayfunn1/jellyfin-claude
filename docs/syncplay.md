# SyncPlay: watching together

SyncPlay keeps playback in step for people watching on different devices, in the same room or across the internet. One
person starts something and everyone's player starts it too. When anyone pauses, seeks or skips to the next episode,
everyone does. When someone's player has to buffer, the others wait for it.

The server keeps the group's state: what is queued, where playback is, and who is ready. It tells each player what to do
and when. Every player keeps its clock in step with the server, so a "resume at 21:04:07.250" lands at the same moment
everywhere.

## Before you start

SyncPlay needs four things. When it "just doesn't work", one of these is usually missing:

1. **SyncPlay is allowed for every participant.** Administrators set this per user: *Dashboard → Users → (user) →
   SyncPlay access*. The options are *create and join groups* (the default), *join groups only*, or *disabled*. A user
   with access disabled does not get the SyncPlay button.
2. **Everyone can see the content.** All members of a group must have access to everything in the group's queue,
   including the library and the parental rating. If one member cannot see an item, the group cannot play it. The
   server log names who is missing access:
   `Group … cannot play the requested items: user(s) … cannot access all of them.` A user without access to what a group
   is playing does not see that group in the list either.
3. **WebSockets reach the server.** The server pushes every SyncPlay command to the players over the WebSocket the
   client keeps open. If Jellyfin is behind a reverse proxy that does not pass WebSockets through, the client cannot
   create or join a group. The server refuses with *"SyncPlay needs a WebSocket connection between this client and the
   server…"* and logs `Refusing SyncPlay for session …: it has no open WebSocket connection`. See
   [Reverse proxies](#reverse-proxies).
4. **A client that supports SyncPlay.** SyncPlay is part of the web client. That covers browsers, Jellyfin Media Player
   and the apps built around the web client, such as Jellyfin for Android. Apps with their own player (for example
   Swiftfin, Findroid, Infuse or Kodi add-ons) generally do not implement it; check the app.

Every participant needs their own session. **Two tabs of the same browser are one device to the server**, so they cannot
sync with each other. To try SyncPlay on your own, use two different browsers, a private window, or two devices.

## When to turn it on, and how

It does not matter who turns SyncPlay on first, or whether something is already playing:

1. **One person creates a group.** In the web client, open the SyncPlay menu (the group icon in the top bar) and choose
   to create a new group. If you are already watching something, the group takes over what you are watching, at your
   current position.
2. **The others join it** from the same menu. A member joining a group that is already playing loads the same item at the
   group's position. Everyone pauses briefly while the joiner catches up. If the group has nothing to play yet and the
   joiner is watching something, the group takes over the joiner's playback, and everyone else starts it too.
3. **Play anything as usual.** While you are in a group, starting a movie, an episode or a playlist starts it for
   everyone. Play/pause, seeking, next/previous, the play queue, shuffle and repeat are shared by the whole group.
4. **Leave** from the SyncPlay menu when you are done. A group without members disappears.

So turn it on whenever you want to start watching together, before or after pressing play. Turn it off by leaving the
group when you want your player to yourself again.

## What happens while watching

- **Someone buffers.** The group pauses everyone at the same position and resumes when the buffering player is ready.
  If that player is a little behind, the others wait until it catches up, and then everyone carries on together.
- **Someone's player never gets ready**, e.g. it cannot play the file. The group does not wait forever: after 30 seconds
  it carries on without that member. A group that was paused stays paused at the same position. The member can leave
  and rejoin to catch up.
- **A player cannot land exactly on the group's position.** Transcoded streams, for example, restart on segment
  boundaries. The server corrects such a player a few times, then accepts its position. From then on the client's own
  sync correction keeps it in step.
- **Someone pauses while the group waits for a buffering member.** Once everyone is ready, the group stays paused where
  the wait began.

In the web client, the SyncPlay menu also has settings for how the player stays in sync. A lagging player can either
play slightly faster or skip ahead, and you can add a fixed time offset for a device that is consistently early or
late.

## Reverse proxies

The web client opens its WebSocket at `/socket`. The proxy has to forward the WebSocket upgrade for it.

**nginx**: forward the upgrade headers. The block below can serve the whole site, or only `location /socket`:

```nginx
location / {
    proxy_pass http://127.0.0.1:8096;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

**Apache**: enable `mod_proxy_wstunnel` and forward `/socket` as `ws://`:

```apache
ProxyPass "/socket" "ws://127.0.0.1:8096/socket"
ProxyPassReverse "/socket" "ws://127.0.0.1:8096/socket"
ProxyPass "/" "http://127.0.0.1:8096/"
ProxyPassReverse "/" "http://127.0.0.1:8096/"
```

**Caddy and Traefik** forward WebSockets without extra configuration. **Cloudflare** has a WebSockets switch in the
network settings of the zone; it must be on.

## Troubleshooting

| What you see | Why | What to do |
|---|---|---|
| No SyncPlay button | SyncPlay access is disabled for the user, or the app does not support SyncPlay | Check *SyncPlay access* for the user; use the web client |
| Creating or joining fails with *"SyncPlay needs a WebSocket connection…"* | The WebSocket does not reach the server | Fix the reverse proxy, see above |
| A group does not show up in the list | The user cannot access something the group is playing | Give the user access to that library, or check its parental control settings |
| *"Access to this content is restricted"* | Same, when joining or when a member starts something | As above; the server log names the users |
| Two tabs on one computer do not sync | Same browser, same session | Use two browsers or devices |
| The group keeps pausing | A member's connection or device cannot keep up, often while transcoding | Lower that member's streaming quality, or let it direct play |
| Playback drifts apart over time | Sync correction is off in a client | Turn it on in the SyncPlay settings of that client |

The server logs every SyncPlay request and state change at the *Information* level, under
`Emby.Server.Implementations.SyncPlay` and `MediaBrowser.Controller.SyncPlay`. Problems with a member are logged as
warnings, e.g. `Group … waited … ms for session(s) … to report ready, giving up.`
