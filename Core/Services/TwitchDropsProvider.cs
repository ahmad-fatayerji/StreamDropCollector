using System.Text.Json.Nodes;
using Core.Interfaces;
using Core.Logging;
using Core.Models;
using Core.Enums;

namespace Core.Services
{
    /// <summary>
    /// Provides access to Twitch drops campaigns and related functionality.
    /// </summary>
    /// <remarks>Use this provider to retrieve active Twitch drops campaigns for integration with
    /// drops-enabled applications or services. This class is intended to be used in conjunction with a compatible web
    /// view host to access Twitch campaign data.</remarks>
    public class TwitchDropsProvider(IGqlService gql) : DropsCampaignProviderBase
    {
        private const int MaxTwitchStreamerAvailabilityLookupsPerCampaign = 150;

        /// <summary>
        /// Gets the platform associated with this instance.
        /// </summary>
        public override Platform Platform => Platform.Twitch;

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
                AppLogger.Info("TwitchDrops", "Fetching active campaigns started.");
                await host.EnsureInitializedAsync();

                JsonArray dashboard = await gql.QueryFullDropsDashboardAsync(ct);

                JsonObject ongoingCampaigns = dashboard[0]!.AsObject();
                JsonObject activeCampaigns = dashboard[1]!.AsObject();

                JsonArray? campaigns = activeCampaigns["data"]?["currentUser"]?["dropCampaigns"]?.AsArray();

                string userId = activeCampaigns["data"]?["currentUser"]?["id"]?.GetValue<string>() ?? "";
                gql.UserId = userId;

                campaigns?.RemoveAll(campaign =>
                {
                    if (campaign is not JsonObject campaignObj)
                        return true; // Remove

                    // Remove if status != "ACTIVE"
                    if (!campaignObj.TryGetPropertyValue("status", out JsonNode? statusNode) ||
                        statusNode?.GetValue<string>() != "ACTIVE")
                        return true;

                    // Remove if not connected
                    if (!campaignObj.TryGetPropertyValue("self", out JsonNode? selfNode) ||
                        selfNode is not JsonObject selfObj ||
                        !selfObj.TryGetPropertyValue("isAccountConnected", out JsonNode? connectedNode) ||
                        connectedNode?.GetValue<bool>() != true)
                        return true; // Remove

                    return false; // Keep
                });

                AppLogger.Info("TwitchDrops", $"Campaigns after status/account filter: count={campaigns?.Count ?? 0}");

                if (campaigns == null || campaigns.Count == 0)
                {
                    AppLogger.Warn("TwitchDrops", "No Twitch campaigns remained after filtering.");
                    return [];
                }

                List<DropsCampaign> result = new List<DropsCampaign>();
                foreach (JsonObject camp in campaigns.OfType<JsonObject>())
                {
                    DropsCampaign? dropCampaign = ParseCampaignFromDetails(camp);

                    if (dropCampaign != null)
                        result.Add(dropCampaign);
                }

                JsonArray dropCampaignsInProgress = ongoingCampaigns["data"]?["currentUser"]?["inventory"]?["dropCampaignsInProgress"]?.AsArray() ?? new JsonArray();
                JsonArray gameEventDrops = ongoingCampaigns["data"]?["currentUser"]?["inventory"]?["gameEventDrops"]?.AsArray() ?? new JsonArray(); // Already finished/claimed drops
                LogInventoryCampaignShape(dropCampaignsInProgress);

                List<DropsCampaign> inventoryCampaigns = dropCampaignsInProgress
                    .OfType<JsonObject>()
                    .Select(ParseCampaignFromDetails)
                    .OfType<DropsCampaign>()
                    .ToList();
                AppLogger.Info("TwitchDrops", $"Parsed inventory campaigns in progress. count={inventoryCampaigns.Count}, rewards={inventoryCampaigns.Sum(c => c.Rewards.Count)}");

                List<DropsCampaign> mergedCampaigns = MergeInventoryCampaigns(result, inventoryCampaigns);
                List<DropsCampaign> updatedResult = ApplyProgressToCampaigns(mergedCampaigns, dropCampaignsInProgress, gameEventDrops);

                if (!updatedResult.Any(c => c.Rewards.Count > 0))
                {
                    AppLogger.Warn("TwitchDrops", "Dashboard campaigns did not include usable reward details; falling back to DropCampaignDetails query.");

                    List<(string dropID, string channelLogin)> requests = new List<(string dropID, string channelLogin)>();

                    foreach (JsonNode? campaign in campaigns)
                    {
                        if (campaign is not JsonObject campObj)
                            continue;

                        string? dropId = campObj.TryGetPropertyValue("id", out JsonNode? idNode) ? idNode?.GetValue<string>() : null;
                        if (string.IsNullOrEmpty(dropId))
                            continue;

                        requests.Add((dropId, userId));
                    }

                    if (requests.Count == 0)
                    {
                        AppLogger.Debug("TwitchDrops", "No valid dropIDs to query.");
                        AppLogger.Warn("TwitchDrops", "No valid drop IDs available for details query.");
                        return [];
                    }

                    using CancellationTokenSource detailsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    detailsTimeout.CancelAfter(TimeSpan.FromSeconds(20));

                    Dictionary<string, JsonObject> campaignDetails = await gql.QueryDropCampaignDetailsBatchAsync(requests, detailsTimeout.Token);

                    AppLogger.Debug("TwitchDrops", $"Successfully fetched detailed data for {campaignDetails.Count} campaigns.");
                    AppLogger.Info("TwitchDrops", $"Campaign details fetched. requested={requests.Count}, received={campaignDetails.Count}");

                    result.Clear();
                    foreach (JsonObject camp in campaignDetails.Values)
                    {
                        DropsCampaign? dropCampaign = ParseCampaignFromDetails(camp);

                        if (dropCampaign != null)
                            result.Add(dropCampaign);
                    }

                    mergedCampaigns = MergeInventoryCampaigns(result, inventoryCampaigns);
                    updatedResult = ApplyProgressToCampaigns(mergedCampaigns, dropCampaignsInProgress, gameEventDrops);
                }
                else
                {
                    AppLogger.Info("TwitchDrops", $"Using dashboard campaign payload directly. count={updatedResult.Count}");
                }

                if (updatedResult.Count == 0)
                {
                    AppLogger.Warn("TwitchDrops", "No Twitch campaigns could be parsed with usable rewards.");
                    return [];
                }

                campaignsFetchedBeforeStreamerStatus?.Invoke(updatedResult.AsReadOnly());

                IReadOnlyList<DropsCampaign> campaignsWithStreamerStatus = await AddTwitchStreamerAvailabilityAsync(updatedResult, ct);

                AppLogger.Info("TwitchDrops", $"Active campaigns fetched successfully. count={campaignsWithStreamerStatus.Count}");
                return campaignsWithStreamerStatus;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TwitchDrops", "Fetching active campaigns failed.", ex);
                return [];
            }
        }

        private static List<DropsCampaign> MergeInventoryCampaigns(
            IReadOnlyList<DropsCampaign> activeCampaigns,
            IReadOnlyList<DropsCampaign> inventoryCampaigns)
        {
            Dictionary<string, DropsCampaign> activeById = activeCampaigns
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

            List<DropsCampaign> merged = new List<DropsCampaign>(activeCampaigns.Count + inventoryCampaigns.Count);
            HashSet<string> addedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DropsCampaign activeCampaign in activeCampaigns)
            {
                DropsCampaign? inventoryCampaign = inventoryCampaigns
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Id) && string.Equals(c.Id, activeCampaign.Id, StringComparison.OrdinalIgnoreCase));

                merged.Add(inventoryCampaign == null
                    ? activeCampaign
                    : MergeCampaignMetadata(activeCampaign, inventoryCampaign));

                if (!string.IsNullOrWhiteSpace(activeCampaign.Id))
                    addedIds.Add(activeCampaign.Id);
            }

            foreach (DropsCampaign inventoryCampaign in inventoryCampaigns)
            {
                if (!string.IsNullOrWhiteSpace(inventoryCampaign.Id) && addedIds.Contains(inventoryCampaign.Id))
                    continue;

                if (activeById.TryGetValue(inventoryCampaign.Id, out DropsCampaign? activeCampaign))
                    merged.Add(MergeCampaignMetadata(activeCampaign, inventoryCampaign));
                else
                    merged.Add(inventoryCampaign);
            }

            return merged;
        }

        private static DropsCampaign MergeCampaignMetadata(DropsCampaign activeCampaign, DropsCampaign inventoryCampaign)
        {
            return inventoryCampaign with
            {
                Name = string.IsNullOrWhiteSpace(inventoryCampaign.Name) || inventoryCampaign.Name == "Unknown Campaign"
                    ? activeCampaign.Name
                    : inventoryCampaign.Name,
                Slug = string.IsNullOrWhiteSpace(inventoryCampaign.Slug) || inventoryCampaign.Slug == "Unknown Game"
                    ? activeCampaign.Slug
                    : inventoryCampaign.Slug,
                GameName = string.IsNullOrWhiteSpace(inventoryCampaign.GameName) || inventoryCampaign.GameName == "Unknown Game"
                    ? activeCampaign.GameName
                    : inventoryCampaign.GameName,
                GameImageUrl = string.IsNullOrWhiteSpace(inventoryCampaign.GameImageUrl)
                    ? activeCampaign.GameImageUrl
                    : inventoryCampaign.GameImageUrl,
                ConnectUrls = inventoryCampaign.ConnectUrls.Count > 0
                    ? inventoryCampaign.ConnectUrls
                    : activeCampaign.ConnectUrls,
                Streamers = inventoryCampaign.Streamers.Count > 0
                    ? inventoryCampaign.Streamers
                    : activeCampaign.Streamers,
                IsGeneralDrop = inventoryCampaign.Streamers.Count > 0
                    ? inventoryCampaign.IsGeneralDrop
                    : activeCampaign.IsGeneralDrop
            };
        }

        private static void LogInventoryCampaignShape(JsonArray dropCampaignsInProgress)
        {
            if (dropCampaignsInProgress.Count == 0)
            {
                AppLogger.Info("TwitchDrops", "Inventory campaigns in progress: count=0");
                return;
            }

            JsonObject? firstCampaign = dropCampaignsInProgress.OfType<JsonObject>().FirstOrDefault();
            string keys = firstCampaign == null ? "" : GetJsonKeys(firstCampaign);
            AppLogger.Info("TwitchDrops", $"Inventory campaigns in progress: count={dropCampaignsInProgress.Count}, firstKeys={keys}");
        }

        private static string GetJsonKeys(JsonObject obj)
        {
            List<string> keys = new();
            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                keys.Add(property.Key);
                if (keys.Count >= 20)
                    break;
            }

            return string.Join(",", keys);
        }

        private static List<DropsCampaign> ApplyProgressToCampaigns(
            IReadOnlyList<DropsCampaign> campaigns,
            JsonArray dropCampaignsInProgress,
            JsonArray gameEventDrops)
        {
                List<DropsCampaign> updatedResult = new List<DropsCampaign>();

                foreach (DropsCampaign dropCampaign in campaigns)
                {
                    // Find matching progress for this campaign (in-progress data)
                    JsonObject? matchingProgress = dropCampaignsInProgress.OfType<JsonObject>().FirstOrDefault(c => c["id"]?.GetValue<string>() == dropCampaign.Id);

                    JsonArray? timeBasedDropsProgress = matchingProgress?["timeBasedDrops"]?.AsArray();

                    List<DropsReward> sourceRewards = dropCampaign.Rewards.ToList();
                    if (sourceRewards.Count == 0 && timeBasedDropsProgress != null)
                    {
                        sourceRewards = ParseRewards(timeBasedDropsProgress);
                        if (sourceRewards.Count > 0)
                            AppLogger.Info("TwitchDrops", $"Built Twitch rewards from inventory progress. campaign='{dropCampaign.Name}', rewards={sourceRewards.Count}");
                    }

                    // Prepare updated rewards list for this campaign
                    List<DropsReward> updatedRewardsForThisCampaign = new List<DropsReward>();

                    foreach (DropsReward reward in sourceRewards)
                    {
                        DropsReward updatedReward = reward; // Start with original

                        // 1. Apply in-progress data if available (progress minutes + claim status)
                        if (timeBasedDropsProgress != null)
                        {
                            JsonObject? matchingDropProgress = timeBasedDropsProgress.OfType<JsonObject>()
                                .FirstOrDefault(d => d["id"]?.GetValue<string>() == reward.Id);

                            if (matchingDropProgress != null)
                            {
                                int progressMinutes = matchingDropProgress["self"]?["currentMinutesWatched"]?.GetValue<int>() ?? reward.ProgressMinutes;
                                bool isClaimed = matchingDropProgress["self"]?["isClaimed"]?.GetValue<bool>() ?? reward.IsClaimed;

                                updatedReward = updatedReward with
                                {
                                    ProgressMinutes = progressMinutes,
                                    IsClaimed = isClaimed
                                };
                            }
                        }

                        // 2. Apply gameEventDrops (completed drops) - these mark rewards as claimed via DropInstanceId
                        JsonObject? matchingEventDrop = gameEventDrops.OfType<JsonObject>()
                            .FirstOrDefault(e => e["id"]?.GetValue<string>() == reward.DropInstanceId);

                        if (matchingEventDrop != null)
                        {
                            // This reward has been fully claimed via a game event drop
                            updatedReward = updatedReward with
                            {
                                IsClaimed = true,
                                ProgressMinutes = reward.RequiredMinutes // Mark as fully completed
                            };
                        }

                        updatedRewardsForThisCampaign.Add(updatedReward);
                    }

                    // Create new campaign with updated rewards
                    DropsCampaign updatedCampaign = dropCampaign with
                    {
                        Rewards = updatedRewardsForThisCampaign.AsReadOnly()
                    };

                    updatedResult.Add(updatedCampaign);
                }

                return updatedResult;
        }

        private async Task<IReadOnlyList<DropsCampaign>> AddTwitchStreamerAvailabilityAsync(IReadOnlyList<DropsCampaign> campaigns, CancellationToken ct)
        {
            List<DropsCampaign> updatedCampaigns = new(campaigns.Count);

            foreach (DropsCampaign campaign in campaigns)
            {
                if (campaign.Streamers.Count == 0 || string.IsNullOrWhiteSpace(campaign.Slug))
                {
                    updatedCampaigns.Add(campaign);
                    continue;
                }

                List<string> allLogins = campaign.Streamers
                    .Select(s => s.Login)
                    .Where(login => !string.IsNullOrWhiteSpace(login))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (allLogins.Count == 0)
                {
                    updatedCampaigns.Add(campaign);
                    continue;
                }

                try
                {
                    List<string> logins = allLogins
                        .Take(MaxTwitchStreamerAvailabilityLookupsPerCampaign)
                        .ToList();

                    int skipped = allLogins.Count - logins.Count;
                    if (skipped > 0)
                        AppLogger.Warn("TwitchDrops", $"Twitch streamer availability lookup capped for campaign '{campaign.Name}'. listedStreamers={campaign.Streamers.Count}, distinct={allLogins.Count}, requested={logins.Count}, skipped={skipped}");

                    List<string> liveLogins = await gql.QueryLiveChannelsBySlugAsync(logins, campaign.Slug, ct);
                    HashSet<string> liveLoginSet = liveLogins.ToHashSet(StringComparer.OrdinalIgnoreCase);

                    List<DropStreamer> updatedStreamers = campaign.Streamers
                        .Select(streamer => streamer with { IsLive = liveLoginSet.Contains(streamer.Login) })
                        .ToList();

                    updatedCampaigns.Add(campaign with { Streamers = updatedStreamers.AsReadOnly() });
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    AppLogger.Warn("TwitchDrops", $"Twitch streamer availability lookup failed for campaign '{campaign.Name}'; leaving unknown statuses. {ex.Message}");
                    updatedCampaigns.Add(campaign);
                }
            }

            AppLogger.Info("TwitchDrops", $"Twitch streamer availability lookup completed. campaigns={campaigns.Count}");
            return updatedCampaigns.AsReadOnly();
        }

        /// <summary>
        /// Creates a new instance of the DropsCampaign class by extracting campaign details from the specified JSON
        /// object.
        /// </summary>
        /// <remarks>If required fields are missing or invalid in the JSON object, default values are used
        /// for those fields. The method assumes the input follows the expected structure for campaign details as
        /// provided by the data source.</remarks>
        /// <param name="detailedData">A JsonObject containing detailed information about the campaign, including identifiers, game data, time
        /// frames, rewards, and channel information. Must not be null and is expected to follow the expected schema for
        /// campaign details.</param>
        /// <returns>A DropsCampaign object populated with data parsed from the provided JSON object. The returned object
        /// contains campaign metadata, associated rewards, and relevant channel URLs.</returns>
        private static DropsCampaign? ParseCampaignFromDetails(JsonObject detailedData)
        {
            string id = detailedData["id"]?.GetValue<string>() ?? "";
            string name = detailedData["name"]?.GetValue<string>()
                ?? detailedData["displayName"]?.GetValue<string>()
                ?? "Unknown Campaign";

            JsonObject? game = detailedData["game"]?.AsObject();
            string gameName = game?["displayName"]?.GetValue<string>()
                ?? game?["name"]?.GetValue<string>()
                ?? "Unknown Game";
            string? gameImage = detailedData["imageURL"]?.GetValue<string>()
                ?? detailedData["imageAssetURL"]?.GetValue<string>()
                ?? game?["boxArtURL"]?.GetValue<string>();

            DateTimeOffset startsAt = DateTimeOffset.Parse(detailedData["startAt"]?.GetValue<string>() ?? DateTimeOffset.UtcNow.ToString("o"));
            DateTimeOffset endsAt = DateTimeOffset.Parse(detailedData["endAt"]?.GetValue<string>() ?? startsAt.AddDays(7).ToString("o"));

            List<string> connectUrls = new List<string>();
            List<DropStreamer> streamers = new List<DropStreamer>();

            JsonObject? allow = detailedData["allow"]?.AsObject();
            JsonArray? channels = allow?["channels"]?.AsArray();

            bool isGeneralDrop = false;
            string slug = game?["slug"]?.GetValue<string>() ?? "Unknown Game";

            if (channels != null)
            {
                foreach (JsonObject channel in channels.OfType<JsonObject>())
                {
                    string? url = channel?["name"]?.GetValue<string>();
                    url = url != null ? $"https://www.twitch.tv/{url}" : null;

                    if (url != null)
                    {
                        connectUrls.Add(url);
                        streamers.Add(new DropStreamer(
                            Login: channel?["name"]?.GetValue<string>() ?? url,
                            Url: url
                        ));
                    }
                }
            }
            else
            {
                string directoryUrl = $"https://www.twitch.tv/directory/category/{slug}?filter=drops&sort=VIEWER_COUNT";
                connectUrls.Add(directoryUrl);
                isGeneralDrop = true;
            }

            JsonArray? timeBasedDrops = detailedData["timeBasedDrops"]?.AsArray()
                ?? detailedData["drops"]?.AsArray()
                ?? detailedData["rewards"]?.AsArray();

            List<DropsReward> rewards = timeBasedDrops == null ? new List<DropsReward>() : ParseRewards(timeBasedDrops);

            if (rewards.Count == 0)
            {
                string keys = GetJsonKeys(detailedData);
                AppLogger.Warn("TwitchDrops", $"Parsed Twitch campaign '{name}' without rewards. Keeping campaign visible. keys={keys}");
            }

            DropsCampaign dropCampaign = new DropsCampaign(
                Id: id,
                Name: name,
                Slug: slug,
                GameName: gameName,
                GameImageUrl: gameImage,
                StartsAt: startsAt,
                EndsAt: endsAt,
                Rewards: rewards.AsReadOnly(),
                Platform: Platform.Twitch,
                ConnectUrls: connectUrls,
                Streamers: streamers.AsReadOnly(),
                IsGeneralDrop: isGeneralDrop
            );

            return dropCampaign;
        }

        private static List<DropsReward> ParseRewards(JsonArray timeBasedDrops)
        {
            List<DropsReward> rewards = new List<DropsReward>();

            foreach (JsonObject drop in timeBasedDrops.OfType<JsonObject>())
            {
                string dropId = drop["id"]?.GetValue<string>() ?? "";
                int requiredMinutes = drop["requiredMinutesWatched"]?.GetValue<int>()
                    ?? drop["requiredMinutes"]?.GetValue<int>()
                    ?? drop["required"]?.GetValue<int>()
                    ?? 0;
                int requiredSubs = drop["requiredSubs"]?.GetValue<int>() ?? 0;

                int currentMinutes = drop["self"]?["currentMinutesWatched"]?.GetValue<int>()
                    ?? drop["currentMinutesWatched"]?.GetValue<int>()
                    ?? drop["progressMinutes"]?.GetValue<int>()
                    ?? 0;
                bool isClaimed = drop["self"]?["isClaimed"]?.GetValue<bool>()
                    ?? drop["isClaimed"]?.GetValue<bool>()
                    ?? false;

                JsonArray? benefitEdges = drop["benefitEdges"]?.AsArray();
                if (benefitEdges != null && benefitEdges.Count > 0)
                {
                    foreach (JsonObject benefitEdge in benefitEdges.OfType<JsonObject>())
                    {
                        JsonObject? benefit = benefitEdge["benefit"]?.AsObject();
                        if (benefit == null)
                            continue;

                        string? benefitId = benefit["id"]?.GetValue<string>();
                        if (benefitId == null)
                            continue;

                        string rewardName = benefit["name"]?.GetValue<string>()
                                            ?? drop["name"]?.GetValue<string>()
                                            ?? "Unknown Reward";

                        string rewardImage = benefit["imageAssetURL"]?.GetValue<string>() ?? "";

                        if (requiredMinutes > 0 || requiredSubs > 0)
                            rewards.Add(new DropsReward(
                                Id: dropId,
                                Name: rewardName,
                                ImageUrl: rewardImage,
                                RequiredMinutes: requiredMinutes,
                                ProgressMinutes: currentMinutes,
                                IsClaimed: isClaimed,
                                DropInstanceId: benefitId
                            ));
                    }
                }
                else if (requiredMinutes > 0 || requiredSubs > 0)
                {
                    string rewardName = drop["name"]?.GetValue<string>()
                        ?? drop["displayName"]?.GetValue<string>()
                        ?? "Twitch Drop";

                    string rewardImage = drop["imageURL"]?.GetValue<string>()
                        ?? drop["imageAssetURL"]?.GetValue<string>()
                        ?? "";

                    rewards.Add(new DropsReward(
                        Id: dropId,
                        Name: rewardName,
                        ImageUrl: rewardImage,
                        RequiredMinutes: requiredMinutes,
                        ProgressMinutes: currentMinutes,
                        IsClaimed: isClaimed
                    ));
                }
            }

            return rewards;
        }

    }
}
