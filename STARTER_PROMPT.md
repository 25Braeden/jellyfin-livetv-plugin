Read CLAUDE.md first. Then do research only, no code yet.

Using the Jellyfin source at ../jellyfin (tag v10.11.11), answer these with file paths and
line numbers:

1. What .NET target framework does 10.11.11 use, and what SDK version do I need?
2. When a client opens a Live TV stream, what is the full call path from the HTTP endpoint
   down to the tuner host / M3U tuner code? Is the authenticated user available at any
   point in that path, and where?
3. What hooks can a plugin use to (a) filter the Live TV channel list and guide per user,
   (b) intercept or veto a live stream open, (c) register a custom tuner host? Which of
   these give us the user identity?
4. How does Jellyfin track concurrent live streams and tuner assignment today?
5. Based on 1-4: is the plugin approach viable for per-user tuner assignment, channel
   filtering, and a one-stream-per-user limit? Or should we fall back to the reverse-proxy
   approach? Give a clear recommendation and the main risks.

Finish with a short written spec for Decision Gate 1 (hello-world plugin) and tell me what
to install on my Mac. Do not install anything yet.
