{
  "version": "1.0.15",
  "type": "Patch",
  "description": "Improved Twitch reliability and inventory responsiveness.",
  "changelog": "Added persistent GQL hash caching with retry-aware fallback, immediate claimed-badge inventory updates after auto-claim, remembered streamer selection, and verbose-gated debug/cache logging.",
  "historic_versions": [
    {
      "version": "1.0.14",
      "type": "Patch",
      "changelog": "Improved WebView waiting reliability and dispatcher async handling."
    },
    {
      "version": "1.0.13",
      "type": "Patch",
      "changelog": "Added verbose debug toggle in Settings, improved Twitch/Kick selection diagnostics, and fixed reward progress percentage tracking while keeping campaign progress tracking intact."
    },
    {
      "version": "1.0.12",
      "type": "Patch",
      "changelog": "Added comprehensive diagnostic logging and a new 'Open Logs Folder' option in Settings"
    },
    {
      "version": "1.0.11",
      "type": "Patch",
      "changelog": "Added comprehensive diagnostic logging and a new 'Open Logs Folder' option in Settings"
    },
    {
      "version": "1.0.10",
      "type": "Patch",
      "changelog": "Fixed WebView contention during drops refresh and stream watching"
    },
    {
      "version": "1.0.9",
      "type": "Patch",
      "changelog": "Fixed a minor issue where start minimized didn't take effect"
    },
    {
      "version": "1.0.8",
      "type": "Patch",
      "changelog": "Highlight current campaign/reward with \"WATCHING\" badges"
    },
    {
      "version": "1.0.7",
      "type": "Patch",
      "changelog": "Improved stream selection and monitoring to verify that the watched Twitch or Kick stream matches the required game/category for each drops campaign"
    },
    {
      "version": "1.0.6",
      "type": "Patch",
      "changelog": "Fixed a bug in prioritizing streamers to watch before general drops"
    },
    {
      "version": "1.0.5",
      "type": "Patch",
      "changelog": "fixed counting error (%) and bug where after claiming all campaigns it would take an hour to idle again.."
    },
    {
      "version": "1.0.4",
      "type": "Patch",
      "changelog": "Fixed a few minor issues with claim status for twitch rewards."
    },
    {
      "version": "1.0.3",
      "type": "Patch",
      "changelog": "Added drop progress to the dashboard."
    },
    {
      "version": "1.0.2",
      "type": "Patch",
      "changelog": "Github Directory Downloader module updated."
    },
    {
      "version": "1.0.1",
      "type": "Bugfix",
      "changelog": "Added Kick bearer token for claim request."
    },
    {
      "version": "1.0.0",
      "type": "Release",
      "changelog": "Initial Release."
    }
  ]
}