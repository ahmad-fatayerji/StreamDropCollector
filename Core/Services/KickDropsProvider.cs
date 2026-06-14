using System.Text.Json;
using Core.Logging;
using Core.Interfaces;
using Core.Models;
using Core.Enums;

namespace Core.Services
{
    /// <summary>
    /// Provides an implementation of a drops campaign provider for the Kick platform.
    /// </summary>
    /// <remarks>Use this class to retrieve active drops campaigns available on Kick. Inherits common
    /// functionality from DropsCampaignProviderBase and specializes it for the Kick platform.</remarks>
    public class KickDropsProvider : DropsCampaignProviderBase
    {
        private const int MaxKickStreamerAvailabilityLookups = 150;
        private const int KickStreamerAvailabilityBatchSize = 25;

        /// <summary>
        /// Gets the streaming platform associated with this instance.
        /// </summary>
        public override Platform Platform => Platform.Kick;
        /// <summary>
        /// Asynchronously retrieves the list of currently active drops campaigns from Kick, including associated
        /// channels, rewards, and the user's progress and claim status for each reward.
        /// </summary>
        /// <remarks>This method combines campaign metadata with the user's progress and claim status for
        /// each reward. The returned campaigns reflect the current state as seen on Kick. The operation may take
        /// several seconds to complete, depending on network conditions and site responsiveness.</remarks>
        /// <param name="host">The web view host used to interact with the Kick website and capture campaign and progress data. Must be
        /// initialized before calling this method.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A read-only list of active drops campaigns, each containing campaign details, available channels, rewards,
        /// and the user's progress and claim status. Returns an empty list if no active campaigns are found.</returns>
        public override async Task<IReadOnlyList<DropsCampaign>> GetActiveCampaignsAsync(IWebViewHost host, CancellationToken ct = default)
        {
            return await GetActiveCampaignsAsync(host, ct, campaignsFetchedBeforeStreamerStatus: null);
        }

        public async Task<IReadOnlyList<DropsCampaign>> GetActiveCampaignsAsync(
            IWebViewHost host,
            CancellationToken ct = default,
            Action<IReadOnlyList<DropsCampaign>>? campaignsFetchedBeforeStreamerStatus = null)
        {
            try
            {
                AppLogger.Info("KickDrops", "Fetching active campaigns started.");
                await host.EnsureInitializedAsync();

                // 1. Get full campaign data (Channels, Rewards, etc.)
                await host.NavigateAsync("https://web.kick.com/api/v1/drops/campaigns");

                string rawJson = await host.ExecuteScriptAsync("document.body.innerText");

                // Step 1: decode the JSON string safely
                string? json = JsonSerializer.Deserialize<string>(rawJson);

                if (string.IsNullOrWhiteSpace(json))
                {
                    AppLogger.Warn("KickDrops", "Kick campaign API returned empty payload.");
                    return Array.Empty<DropsCampaign>().AsReadOnly();
                }

                // Step 2: parse actual JSON
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement dataArray))
                {
                    AppLogger.Warn("KickDrops", "Kick campaign API payload missing 'data' property.");
                    return Array.Empty<DropsCampaign>().AsReadOnly();
                }

                List<DropsCampaign> campaigns = new List<DropsCampaign>();

                foreach (JsonElement campaign in dataArray.EnumerateArray())
                {
                    if (!campaign.TryGetProperty("status", out JsonElement status) || status.GetString() != "active")
                        continue;

                    campaign.TryGetProperty("category", out JsonElement category);

                    DropsReward[] rewards = [.. campaign.GetProperty("rewards")
                        .EnumerateArray()
                        .Select(r => new DropsReward(
                            Id: r.GetProperty("id").GetString()!,
                            Name: r.GetProperty("name").GetString()!,
                            ImageUrl: "https://ext.cdn.kick.com/" + r.GetProperty("image_url").GetString(),
                            RequiredMinutes: r.GetProperty("required_units").GetInt32()
                        ))];

                    if (rewards.Length == 0)
                        continue;

                    // All the available channels for this campaign
                    List<string> connectUrls = new List<string>();
                    List<DropStreamer> streamers = new List<DropStreamer>();

                    if (campaign.TryGetProperty("channels", out JsonElement channels) && channels.GetArrayLength() > 0)
                    {
                        foreach (JsonElement channel in channels.EnumerateArray())
                        {
                            string? username = channel.TryGetProperty("slug", out JsonElement slug)
                                ? slug.GetString()
                                : channel.GetProperty("user").GetProperty("username").GetString();

                            if (!string.IsNullOrEmpty(username))
                            {
                                string channelUrl = $"https://kick.com/{username.ToLowerInvariant()}";
                                connectUrls.Add(channelUrl);
                                streamers.Add(new DropStreamer(username, channelUrl, TryReadKickLiveStatus(channel)));
                            }
                        }
                    }

                    bool general = false;

                    // Category-less campaigns (e.g. Watch ANYONE, in any category)
                    if (category.ValueKind == JsonValueKind.Undefined && connectUrls.Count == 0)
                    {
                        const string directoryUrl = "https://kick.com/browse?sort=viewers_high_to_low";
                        connectUrls.Add(directoryUrl);
                        general = true;
                    }

                    // General drops = watch ANYONE in category
                    if (connectUrls.Count == 0 && category.ValueKind != JsonValueKind.Undefined)
                    {
                        string slug = category.GetProperty("slug").GetString()!;
                        string directoryUrl = $"https://kick.com/category/{slug}/drops";
                        connectUrls.Add(directoryUrl);
                        general = true;
                    }

                    // Remove duplicates
                    connectUrls = [.. connectUrls.Distinct()];

                    if (category.ValueKind == JsonValueKind.Undefined)
                    {
                        JsonElement organization = campaign.GetProperty("organization");

                        campaigns.Add(new DropsCampaign(
                            Id: campaign.GetProperty("id").GetString()!,
                            Name: campaign.GetProperty("name").GetString()!,
                            Slug: "",
                            GameName: organization.GetProperty("name").GetString()!,
                            GameImageUrl: organization.GetProperty("logo_url").GetString(),
                            StartsAt: DateTimeOffset.Parse(campaign.GetProperty("starts_at").GetString()!),
                            EndsAt: DateTimeOffset.Parse(campaign.GetProperty("ends_at").GetString()!),
                            Rewards: rewards,
                            Platform: Platform,
                            ConnectUrls: connectUrls.AsReadOnly(),
                            Streamers: streamers.AsReadOnly(),
                            IsGeneralDrop: general
                        ));
                    }
                    else
                    {
                        campaigns.Add(new DropsCampaign(
                            Id: campaign.GetProperty("id").GetString()!,
                            Name: campaign.GetProperty("name").GetString()!,
                            Slug: category.GetProperty("slug").GetString()!,
                            GameName: category.GetProperty("name").GetString()!,
                            GameImageUrl: category.GetProperty("image_url").GetString(),
                            StartsAt: DateTimeOffset.Parse(campaign.GetProperty("starts_at").GetString()!),
                            EndsAt: DateTimeOffset.Parse(campaign.GetProperty("ends_at").GetString()!),
                            Rewards: rewards,
                            Platform: Platform,
                            ConnectUrls: connectUrls.AsReadOnly(),
                            Streamers: streamers.AsReadOnly(),
                            IsGeneralDrop: general
                        ));
                    }
                }

                // 2. Get progress + claimed status
                string rawProgress = string.Empty;
                const int maxProgressAttempts = 3;

                for (int attempt = 1; attempt <= maxProgressAttempts && string.IsNullOrEmpty(rawProgress); attempt++)
                {
                    try
                    {
                        rawProgress = await host.CaptureProgressResponseAsync(10000, ct);
                    }
                    catch (TimeoutException ex)
                    {
                        bool willRetry = attempt < maxProgressAttempts;
                        AppLogger.Warn("KickDrops", $"Progress capture timed out (attempt {attempt}/{maxProgressAttempts}); {(willRetry ? "forcing refresh and retrying." : "continuing without progress.")} {ex.Message}");
                        if (willRetry)
                            await host.ForceRefreshAsync();
                    }
                }

                // 3. Merge progress into campaigns (skip if no payload was captured).
                if (!string.IsNullOrEmpty(rawProgress))
                {
                    using JsonDocument progressDoc = JsonDocument.Parse(rawProgress);

                    if (progressDoc.RootElement.TryGetProperty("data", out JsonElement progressArray))
                    {
                        foreach (JsonElement item in progressArray.EnumerateArray())
                        {
                            string campaignId = item.GetProperty("id").GetString()!;

                            foreach (JsonElement reward in item.GetProperty("rewards").EnumerateArray())
                            {
                                string rewardId = reward.GetProperty("id").GetString()!;

                                DropsCampaign? campaign = campaigns.FirstOrDefault(c => c.Id == campaignId);
                                if (campaign == null)
                                    continue;

                                DropsReward? targetReward = campaign.Rewards.FirstOrDefault(r => r.Id == rewardId);
                                if (targetReward == null)
                                    continue;

                                // UPDATE IN-PLACE
                                targetReward = targetReward with
                                {
                                    ProgressMinutes = Math.Min(item.GetProperty("progress_units").GetInt32(), targetReward.RequiredMinutes),
                                    IsClaimed = reward.GetProperty("claimed").GetBoolean()
                                };

                                // Replace in list (records are immutable)
                                List<DropsReward> list = [.. campaign.Rewards];
                                int index = list.IndexOf(campaign.Rewards.First(r => r.Id == rewardId));
                                list[index] = targetReward;

                                // Replace in campaign
                                DropsCampaign updatedCampaign = campaign with { Rewards = list.AsReadOnly() };
                                int campIndex = campaigns.IndexOf(campaign);
                                campaigns[campIndex] = updatedCampaign;
                            }
                        }
                    }
                }

                campaignsFetchedBeforeStreamerStatus?.Invoke(campaigns.AsReadOnly());

                IReadOnlyList<DropsCampaign> campaignsWithStreamerStatus = await AddKickStreamerAvailabilityAsync(host, campaigns, ct);

                AppLogger.Debug("KickDrops", $"LOADED {campaignsWithStreamerStatus.Count} campaigns with progress");
                AppLogger.Info("KickDrops", $"Active campaigns fetched successfully. count={campaignsWithStreamerStatus.Count}");
                return campaignsWithStreamerStatus;
            }
            catch (Exception ex)
            {
                AppLogger.Error("KickDrops", "Fetching active campaigns failed.", ex);
                return [];
            }
        }

        private static async Task<IReadOnlyList<DropsCampaign>> AddKickStreamerAvailabilityAsync(IWebViewHost host, IReadOnlyList<DropsCampaign> campaigns, CancellationToken ct)
        {
            List<string> allStreamerLogins = campaigns
                .SelectMany(c => c.Streamers)
                .Select(s => s.Login)
                .Where(login => !string.IsNullOrWhiteSpace(login))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (allStreamerLogins.Count == 0)
            {
                AppLogger.Info("KickDrops", "Kick streamer availability lookup skipped. listedStreamers=0");
                return campaigns;
            }

            try
            {
                List<string> streamerLogins = allStreamerLogins
                    .Take(MaxKickStreamerAvailabilityLookups)
                    .ToList();

                int skipped = allStreamerLogins.Count - streamerLogins.Count;
                if (skipped > 0)
                    AppLogger.Warn("KickDrops", $"Kick streamer availability lookup capped. listedStreamers={campaigns.SelectMany(c => c.Streamers).Count()}, distinct={allStreamerLogins.Count}, requested={streamerLogins.Count}, skipped={skipped}");

                AppLogger.Info("KickDrops", $"Kick streamer availability lookup started. listedStreamers={campaigns.SelectMany(c => c.Streamers).Count()}, requested={streamerLogins.Count}, batchSize={KickStreamerAvailabilityBatchSize}");

                await host.NavigateAsync($"https://kick.com/?availabilityCheck={DateTimeOffset.Now.ToUnixTimeMilliseconds()}");

                Dictionary<string, bool?> statuses = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> errors = new(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < streamerLogins.Count; i += KickStreamerAvailabilityBatchSize)
                {
                    ct.ThrowIfCancellationRequested();

                    List<string> batch = streamerLogins
                        .Skip(i)
                        .Take(KickStreamerAvailabilityBatchSize)
                        .ToList();

                    KickAvailabilityLookupResult? batchResult = await ExecuteKickStreamerAvailabilityBatchAsync(host, batch);
                    if (batchResult?.Statuses == null || batchResult.Statuses.Count == 0)
                    {
                        AppLogger.Warn("KickDrops", $"Kick streamer availability batch returned no statuses. offset={i}, batchSize={batch.Count}");
                        continue;
                    }

                    foreach ((string login, bool? isLive) in batchResult.Statuses)
                        statuses[login] = isLive;

                    foreach ((string login, string error) in batchResult.Errors)
                        errors[login] = error;
                }

                if (statuses.Count == 0)
                {
                    AppLogger.Warn("KickDrops", "Kick streamer availability lookup returned no statuses across all batches.");
                    return campaigns;
                }

                int liveCount = statuses.Count(x => x.Value == true);
                int offlineCount = statuses.Count(x => x.Value == false);
                int unknownCount = statuses.Count(x => x.Value == null);
                string errorSummary = errors.Count > 0
                    ? string.Join("; ", errors.Take(5).Select(x => $"{x.Key}={x.Value}"))
                    : "none";
                AppLogger.Info("KickDrops", $"Kick streamer availability lookup completed. requested={streamerLogins.Count}, resolved={statuses.Count}, live={liveCount}, offline={offlineCount}, unknown={unknownCount}, skipped={skipped}, errors={errorSummary}");

                List<DropsCampaign> updatedCampaigns = new(campaigns.Count);
                foreach (DropsCampaign campaign in campaigns)
                {
                    List<DropStreamer> updatedStreamers = campaign.Streamers
                        .Select(streamer => statuses.TryGetValue(streamer.Login, out bool? isLive)
                            ? streamer with { IsLive = isLive ?? streamer.IsLive }
                            : streamer)
                        .ToList();

                    updatedCampaigns.Add(campaign with { Streamers = updatedStreamers.AsReadOnly() });
                }

                return updatedCampaigns.AsReadOnly();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppLogger.Warn("KickDrops", $"Kick streamer availability lookup failed; leaving unknown statuses. {ex.Message}");
                return campaigns;
            }
        }

        private static async Task<KickAvailabilityLookupResult?> ExecuteKickStreamerAvailabilityBatchAsync(IWebViewHost host, IReadOnlyList<string> streamerLogins)
        {
            string loginJson = JsonSerializer.Serialize(streamerLogins);
            string script = $$"""
                    (() => {
                        const logins = {{loginJson}};
                        const statuses = {};
                        const errors = {};

                        function readLiveStatus(value, depth = 0) {
                            if (!value || typeof value !== 'object' || depth > 4) {
                                return undefined;
                            }

                            for (const key of ['is_live', 'isLive', 'is_online', 'isOnline', 'online']) {
                                if (typeof value[key] === 'boolean') {
                                    return value[key];
                                }
                            }

                            for (const key of ['livestream', 'live_stream', 'current_livestream', 'stream']) {
                                if (Object.prototype.hasOwnProperty.call(value, key)) {
                                    return value[key] !== null && value[key] !== false;
                                }
                            }

                            for (const key of ['data', 'channel', 'user']) {
                                const nested = readLiveStatus(value[key], depth + 1);
                                if (typeof nested === 'boolean') {
                                    return nested;
                                }
                            }

                            return undefined;
                        }

                        for (const login of logins) {
                            try {
                                const xhr = new XMLHttpRequest();
                                xhr.open('GET', `/api/v2/channels/${encodeURIComponent(login)}`, false);
                                xhr.setRequestHeader('Accept', 'application/json');
                                xhr.send();

                                if (xhr.status < 200 || xhr.status >= 300) {
                                    statuses[login] = null;
                                    errors[login] = `HTTP ${xhr.status}`;
                                    continue;
                                }

                                const data = JSON.parse(xhr.responseText || '{}');
                                const status = readLiveStatus(data);
                                if (typeof status === 'boolean') {
                                    statuses[login] = status;
                                } else {
                                    statuses[login] = null;
                                    errors[login] = `No live field. keys=${Object.keys(data || {}).slice(0, 12).join(',')}`;
                                }
                            } catch (err) {
                                statuses[login] = null;
                                errors[login] = err?.name || err?.message || 'fetch failed';
                            }
                        }

                        return JSON.stringify({ statuses, errors });
                    })();
                    """;

            string rawResult = await host.ExecuteScriptAsync(script);
            string? json = DecodeExecuteScriptStringResult(rawResult);
            KickAvailabilityLookupResult? lookupResult = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<KickAvailabilityLookupResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (lookupResult?.Statuses == null || lookupResult.Statuses.Count == 0)
            {
                AppLogger.Warn("KickDrops", $"Kick streamer availability batch returned no statuses. rawResult={TrimForLog(rawResult)}, decoded={TrimForLog(json)}");
            }

            return lookupResult;
        }

        private static bool? TryReadKickLiveStatus(JsonElement channel)
        {
            if (TryReadKickLiveStatusFromElement(channel, out bool? isLive))
                return isLive;

            if (channel.TryGetProperty("user", out JsonElement user) &&
                TryReadKickLiveStatusFromElement(user, out isLive))
                return isLive;

            return null;
        }

        private static bool TryReadKickLiveStatusFromElement(JsonElement element, out bool? value)
        {
            value = null;

            if (TryGetBooleanProperty(element, "is_live", out bool isLive) ||
                TryGetBooleanProperty(element, "isLive", out isLive) ||
                TryGetBooleanProperty(element, "is_online", out isLive) ||
                TryGetBooleanProperty(element, "isOnline", out isLive) ||
                TryGetBooleanProperty(element, "online", out isLive))
            {
                value = isLive;
                return true;
            }

            if (TryReadLiveObjectProperty(element, "livestream", out value) ||
                TryReadLiveObjectProperty(element, "live_stream", out value) ||
                TryReadLiveObjectProperty(element, "current_livestream", out value) ||
                TryReadLiveObjectProperty(element, "stream", out value))
            {
                return true;
            }

            return false;
        }

        private static bool TryReadLiveObjectProperty(JsonElement element, string propertyName, out bool? value)
        {
            value = null;

            if (!element.TryGetProperty(propertyName, out JsonElement property))
                return false;

            value = property.ValueKind switch
            {
                JsonValueKind.Object => true,
                JsonValueKind.Array => property.GetArrayLength() > 0,
                JsonValueKind.Null => false,
                JsonValueKind.False => false,
                JsonValueKind.True => true,
                _ => null
            };

            return value.HasValue;
        }

        private static string TrimForLog(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "<empty>";

            const int maxLength = 500;
            string trimmed = value.Replace(Environment.NewLine, " ");
            return trimmed.Length <= maxLength
                ? trimmed
                : trimmed[..maxLength] + "...";
        }

        private static bool TryGetBooleanProperty(JsonElement element, string propertyName, out bool value)
        {
            value = false;

            if (!element.TryGetProperty(propertyName, out JsonElement property) ||
                property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False)
            {
                return false;
            }

            value = property.GetBoolean();
            return true;
        }

        private static string? DecodeExecuteScriptStringResult(string rawResult)
        {
            if (string.IsNullOrWhiteSpace(rawResult))
                return null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(rawResult);
                return document.RootElement.ValueKind == JsonValueKind.String
                    ? document.RootElement.GetString()
                    : rawResult;
            }
            catch (JsonException)
            {
                return rawResult;
            }
        }

        private sealed class KickAvailabilityLookupResult
        {
            public Dictionary<string, bool?> Statuses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Errors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
