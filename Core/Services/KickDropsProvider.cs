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
            List<string> unknownLogins = campaigns
                .SelectMany(c => c.Streamers)
                .Where(s => s.IsLive == null)
                .Select(s => s.Login)
                .Where(login => !string.IsNullOrWhiteSpace(login))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();

            if (unknownLogins.Count == 0)
                return campaigns;

            try
            {
                string loginJson = JsonSerializer.Serialize(unknownLogins);
                await host.NavigateAsync($"https://kick.com/?availabilityCheck={DateTimeOffset.Now.ToUnixTimeMilliseconds()}");

                string script = $$"""
                    (async () => {
                        const logins = {{loginJson}};
                        const results = {};

                        await Promise.all(logins.map(async login => {
                            const controller = new AbortController();
                            const timeout = setTimeout(() => controller.abort(), 3500);

                            try {
                                const response = await fetch(`/api/v2/channels/${encodeURIComponent(login)}`, {
                                    credentials: 'include',
                                    signal: controller.signal
                                });

                                if (!response.ok) {
                                    results[login] = false;
                                    return;
                                }

                                const data = await response.json();
                                if (typeof data?.is_live === 'boolean') {
                                    results[login] = data.is_live;
                                } else if (typeof data?.isLive === 'boolean') {
                                    results[login] = data.isLive;
                                } else if ('livestream' in data) {
                                    results[login] = data.livestream !== null;
                                } else if ('stream' in data) {
                                    results[login] = data.stream !== null;
                                } else {
                                    results[login] = false;
                                }
                            } catch {
                                results[login] = false;
                            } finally {
                                clearTimeout(timeout);
                            }
                        }));

                        return JSON.stringify(results);
                    })();
                    """;

                string rawResult = await host.ExecuteScriptAsync(script);
                string? json = DecodeExecuteScriptStringResult(rawResult);
                Dictionary<string, bool>? statuses = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, bool>>(json);

                if (statuses == null || statuses.Count == 0)
                    return campaigns;

                AppLogger.Info("KickDrops", $"Kick streamer availability lookup completed. requested={unknownLogins.Count}, resolved={statuses.Count}, live={statuses.Count(x => x.Value)}, offline={statuses.Count(x => !x.Value)}");

                List<DropsCampaign> updatedCampaigns = new(campaigns.Count);
                foreach (DropsCampaign campaign in campaigns)
                {
                    List<DropStreamer> updatedStreamers = campaign.Streamers
                        .Select(streamer => statuses.TryGetValue(streamer.Login, out bool? isLive)
                            ? streamer with { IsLive = isLive }
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
                TryGetBooleanProperty(element, "isLive", out isLive))
            {
                value = isLive;
                return true;
            }

            if (element.TryGetProperty("livestream", out JsonElement livestream))
            {
                value = livestream.ValueKind switch
                {
                    JsonValueKind.Object => true,
                    JsonValueKind.Null => false,
                    _ => null
                };

                return value.HasValue;
            }

            if (element.TryGetProperty("stream", out JsonElement stream))
            {
                value = stream.ValueKind switch
                {
                    JsonValueKind.Object => true,
                    JsonValueKind.Null => false,
                    _ => null
                };

                return value.HasValue;
            }

            return false;
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
    }
}
